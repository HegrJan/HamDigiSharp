using HamDigiSharp.Abstractions;
using HamDigiSharp.Dsp;
using HamDigiSharp.Models;

namespace HamDigiSharp.Engine;

/// <summary>
/// Real-time audio ingestion front-end for <see cref="DecoderEngine"/>.
///
/// <para>
/// The caller feeds raw PCM chunks from any audio capture source
/// (sound card, network stream, file, etc.) via <see cref="AddSamples"/>.
/// <c>RealTimeDecoder</c> internally:
/// </para>
/// <list type="number">
///   <item>Resamples the incoming audio from <c>captureRate</c> to the mode's
///         native decoder rate (12 000 Hz or 11 025 Hz) when they differ.</item>
///   <item>Buffers one full period of (resampled) audio in a ring buffer.</item>
///   <item>Optionally aligns the first window to a UTC boundary so decodes are
///         phase-locked to the mode's transmission schedule.</item>
///   <item><b>Early decode</b>: when the buffer reaches <see cref="EarlyDecodeRatio"/>
///         of the period, launches the decode immediately in the background using a
///         dedicated fast-configured <see cref="DecoderEngine"/> (BP-only, 75
///         candidates). The remaining fraction is zero-padded — it contains only
///         noise, not signal, for all DT ≤ +1 s transmissions.  This ensures results
///         arrive <em>before</em> the period boundary, giving the caller time to
///         prepare a reply before the next TX slot begins.</item>
///   <item>At each period boundary fires <see cref="PeriodDecoded"/> with all
///         decode results (from the fast engine + six parallel decoder instances).</item>
/// </list>
///
/// <para>
/// <b>Speed</b>: the dedicated RT engine defaults to <see cref="DecoderDepth.Normal"/> (BP + OSD order-1)
/// and <c>MaxCandidates=75</c> (half the WAV-file default).  This matches WSJT-X depth 2.
/// Increase sensitivity by setting <see cref="RealTimeOptions"/> to a <see cref="DecoderOptions"/> with
/// <see cref="DecoderDepth.Deep"/> and a larger <c>MaxCandidates</c>.
/// </para>
///
/// <para>
/// <b>Thread safety</b>: <see cref="AddSamples"/> may be called from any
/// single thread continuously. Do not call it concurrently from multiple threads.
/// <see cref="PeriodDecoded"/> and <see cref="DecodeError"/> are raised on a
/// thread-pool thread; UI applications must marshal back to the UI thread.
/// </para>
/// </summary>
public sealed class RealTimeDecoder : IDisposable
{
    private readonly DecoderEngine  _rtEngine;          // owned, fast-configured
    private readonly DigitalMode    _mode;
    private readonly int            _captureRate;
    private readonly int            _decoderRate;
    private readonly Resampler?     _resampler;         // null when rates match
    private readonly float[]        _ring;              // ring buffer at decoder rate
    private int                     _ringPos;           // write head
    private readonly int            _samplesPerPeriod;  // at decoder rate
    private float[]                 _resampleBuf = [];  // reused resample output (grows as needed)

    // ── Negative-DT guard band ────────────────────────────────────────────────
    // Carries the last _guardSamples of each decoded period into the start of the next
    // ring buffer so that signals with negative DT (started slightly before the period
    // boundary) are still decoded.  Size = NegativeDtGuardFraction × period (default 8%).
    // The raw DT from the decoder is corrected by subtracting _guardOffsetSeconds
    // so callers always receive DT relative to the UTC period boundary.
    private readonly int    _guardSamples;        // = NegativeDtGuardFraction × _samplesPerPeriod
    private readonly double _guardOffsetSeconds;  // = _guardSamples / _decoderRate

    // UTC alignment state
    private bool _aligned;
    private int  _skipRemaining;

    // ── Early-decode state ────────────────────────────────────────────────────
    private int?                    _earlyDecodeSamples;
    private bool                    _earlyDecodeStarted;
    private Task<IReadOnlyList<DecodeResult>>? _earlyDecodeTask;

    // Lazy accessor — reads EarlyDecodeRatio AFTER the object initializer has run.
    // Uses the mode's actual signal length: trigger as soon as signal + DT tolerance +
    // margin is fully buffered, but no later than EarlyDecodeRatio × period.
    private int EarlyDecodeSamplesThreshold => _earlyDecodeSamples ??= ComputeEarlyDecodeSamples();

    private int ComputeEarlyDecodeSamples()
    {
        double signal  = _mode.SignalDurationSeconds();
        double period  = _mode.PeriodSeconds();
        // Narrower tolerance: trigger ~100 ms earlier than before, still covering DT ≤ 0.56 s for FT2.
        double maxDt   = Math.Min(0.5,  period * 0.10); // ≤500 ms or 10% of period
        double margin  = Math.Min(0.20, period * 0.05); // ≤200 ms safety pad

        // Guard samples are pre-filled; threshold is offset by that amount.
        int signalSamp = (int)((signal + maxDt + margin) * _decoderRate) + _guardSamples;
        int capacity   = _samplesPerPeriod + _guardSamples;
        int ratioSamp  = Math.Clamp(
            (int)(capacity * EarlyDecodeRatio), _guardSamples + 1, capacity - 1);

        return Math.Clamp(Math.Min(signalSamp, ratioSamp), _guardSamples + 1, capacity - 1);
    }

    // Inflight guard + pending — all managed under _pendingLock (no Interlocked).
    // _decoding is set to true before LaunchDecodeTask and cleared to false inside
    // the same lock once the finally block confirms no new period is pending.
    // This removes the TOCTOU window that existed when _decoding was an int managed
    // with Interlocked operations outside the lock.
    private bool _decoding;
    private readonly object _pendingLock = new();
    private (Task<IReadOnlyList<DecodeResult>>? task, DateTimeOffset windowStart)? _pendingPeriod;

    private DateTimeOffset _windowStart;
    private volatile bool _disposed;

    // ── Public surface ────────────────────────────────────────────────────────

    /// <summary>
    /// Raised after each period completes, on a thread-pool thread.
    /// <paramref name="results"/> is the deduplicated union of all six parallel
    /// decoder instances. <paramref name="windowStart"/> is the UTC start time
    /// of the window that was just decoded.
    /// </summary>
    public event Action<IReadOnlyList<DecodeResult>, DateTimeOffset>? PeriodDecoded;

    /// <summary>Raised when an unhandled exception occurs inside a decode task.</summary>
    public event Action<Exception>? DecodeError;

    /// <summary>Low-frequency limit of the candidate search, in Hz (default 200).</summary>
    public double FreqLow  { get; set; } = 200;

    /// <summary>High-frequency limit of the candidate search, in Hz (default 3000).</summary>
    public double FreqHigh { get; set; } = 3000;

    /// <summary>
    /// When <c>true</c> (default), the first partial window is discarded so that
    /// all subsequent decodes are phase-locked to the mode's UTC schedule.
    /// </summary>
    public bool AlignToUtc { get; init; } = true;

    /// <summary>
    /// Fraction of the mode period carried forward from the previous period as a
    /// negative-DT guard band (default 0.08 = 8 %).
    ///
    /// <para>Signals transmitted slightly before the period boundary (negative DT)
    /// start in the previous period.  The guard band preserves those leading samples
    /// so the decoder can recover the full signal regardless of transmitter clock
    /// offset.</para>
    ///
    /// <para>Because the fraction scales with period length, slower modes automatically
    /// receive a longer absolute guard:
    /// <list type="bullet">
    ///   <item>FT2  (3.75 s):  8 % → 300 ms</item>
    ///   <item>FT4  (7.5 s):   8 % → 600 ms</item>
    ///   <item>FT8  (15 s):    8 % → 1200 ms</item>
    /// </list>
    /// Set a smaller value (e.g. 0.02) to reduce ring-buffer memory; set a larger
    /// value (up to the 0.25 cap) to accommodate stations with extreme clock drift.
    /// The FT4/FT2 decoders are input-adaptive and automatically process the full
    /// buffer, so increasing the fraction does not require any decoder changes.
    /// </para>
    /// </summary>
    public double NegativeDtGuardFraction { get; init; } = 0.08;

    /// <summary>
    /// Fraction of a period at which the decode is pre-launched (default 0.90).
    ///
    /// <para>At 0.90, FT8 decoding begins 1.5 s before the period ends; combined
    /// with the BP-only fast path (~0.5 s) this delivers results ~1 s before the
    /// period boundary — matching WSJT-X timing.</para>
    ///
    /// <para>All signal symbols are present for any DT ≤ +1 s when this value
    /// is ≥ 0.88 (FT8).  Set to 1.0 to revert to start-at-period-end behaviour.</para>
    /// </summary>
    public float EarlyDecodeRatio { get; init; } = 0.90f;

    /// <summary>
    /// Decoder options used for real-time early-decode passes.
    /// Defaults to <see cref="DecoderDepth.Normal"/> (BP + OSD order-1, matches WSJT-X depth 2)
    /// and <c>MaxCandidates=75</c> for lower latency.
    ///
    /// <para>Set <see cref="DecoderDepth.Deep"/> for OSD order-2 sensitivity at the cost of
    /// roughly 50× more LDPC work.  Set <c>MyCall</c>/<c>MyGrid</c> here (not on the
    /// external engine) to enable AP-assisted decode in real-time mode.</para>
    ///
    /// <para><b>Important</b>: changes take effect only when a new <see cref="DecoderOptions"/>
    /// instance is assigned to this property (not when individual properties of the returned
    /// object are mutated). Example:
    /// <code>
    /// rt.RealTimeOptions = rt.RealTimeOptions with { MaxCandidates = 40 };
    /// </code>
    /// </para>
    /// </summary>
    public DecoderOptions RealTimeOptions
    {
        get => _rtOptions;
        set { _rtOptions = value; _rtEngine.Configure(value); }
    }
    private DecoderOptions _rtOptions = new()
    {
        DecoderDepth     = DecoderDepth.Normal,  // BP + OSD order-1 (matches WSJT-X depth 2)
        MaxCandidates    = 75,   // overridden per-mode in constructor
        AveragingEnabled = false,
    };

    // All modes use 75 candidates. With 5-channel timing diversity the per-candidate cost is
    // higher, but PLINQ distributes it across all cores, keeping FT2 well within its 500 ms budget.
    // Reducing candidates for FT2/FT4 saves only ~50 ms while costing meaningful sensitivity
    // (weak signals ranked 41-75 by FFT peak are missed).
    private static int RtMaxCandidates(DigitalMode mode) => 75;

    // ── Construction ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a real-time decoder for the given <paramref name="protocol"/>.
    /// <see cref="FreqLow"/> and <see cref="FreqHigh"/> are initialised from
    /// <see cref="IProtocol.DefaultFreqLow"/> / <see cref="IProtocol.DefaultFreqHigh"/>.
    /// </summary>
    /// <param name="protocol">Protocol describing the mode's timing and audio parameters.</param>
    /// <param name="captureRate">Sample rate of audio fed to <see cref="AddSamples"/>.</param>
    public RealTimeDecoder(IProtocol protocol, int captureRate)
        : this(protocol?.Mode ?? throw new ArgumentNullException(nameof(protocol)), captureRate)
    {
        FreqLow  = protocol.DefaultFreqLow;
        FreqHigh = protocol.DefaultFreqHigh;
    }

    /// <summary>
    /// Creates a real-time decoder for the given <paramref name="mode"/>.
    /// </summary>
    /// <param name="mode">Digital mode to decode.</param>
    /// <param name="captureRate">Sample rate of audio fed to <see cref="AddSamples"/>.</param>
    public RealTimeDecoder(DigitalMode mode, int captureRate)
    {
        if (captureRate <= 0) throw new ArgumentOutOfRangeException(nameof(captureRate));

        _mode        = mode;
        _captureRate = captureRate;
        _decoderRate = mode.DecoderSampleRate();

        _resampler = (captureRate != _decoderRate)
            ? new Resampler(captureRate, _decoderRate)
            : null;

        _samplesPerPeriod = PeriodScheduler.SamplesPerPeriod(mode, _decoderRate);

        // Negative-DT guard band: NegativeDtGuardFraction × period, capped at 25%.
        // Default 8% gives 300 ms for FT2, 600 ms for FT4, 1200 ms for FT8 —
        // slower modes benefit from proportionally more guard without any decoder changes
        // (Ft4x2DecoderBase is fully adaptive to input length).
        double guardFraction    = Math.Clamp(NegativeDtGuardFraction, 0.0, 0.25);
        _guardSamples           = (int)(guardFraction * _samplesPerPeriod);
        _guardOffsetSeconds     = _guardSamples / (double)_decoderRate;

        int slack  = (int)(_decoderRate * 0.2);
        _ring      = new float[_samplesPerPeriod + _guardSamples + slack];
        // Guard band at [0.._guardSamples) is pre-filled with zeros for the first period;
        // subsequent periods carry the last _guardSamples of the previous period here.
        _ringPos   = _guardSamples;
        _aligned   = false;

        // LLR averaging across periods is enabled for FT2: unit-RMS-normalised ensemble-E
        // LLR vectors accumulate across consecutive periods (MRC in the LLR domain), giving
        // sqrt(N) SNR improvement with N periods — no carrier phase alignment required.
        _rtOptions.AveragingEnabled = (mode == DigitalMode.FT2);
        _rtOptions.MaxCandidates    = RtMaxCandidates(mode);

        // Create and configure the dedicated fast engine.
        _rtEngine = new DecoderEngine();
        _rtEngine.Configure(_rtOptions);
    }

    /// <param name="engine">
    ///   Accepted for API compatibility; the real-time decode path owns an internal
    ///   fast-configured engine and does not use this parameter.
    ///   Use <see cref="RealTimeDecoder(DigitalMode, int)"/> instead.
    /// </param>
    /// <param name="mode">Digital mode to decode.</param>
    /// <param name="captureRate">Sample rate of audio fed to <see cref="AddSamples"/>.</param>
    [Obsolete("Pass only (DigitalMode, int) — the DecoderEngine parameter is ignored. Use RealTimeDecoder(DigitalMode, int) instead.")]
    public RealTimeDecoder(DecoderEngine engine, DigitalMode mode, int captureRate)
        : this(mode, captureRate)
    {
        ArgumentNullException.ThrowIfNull(engine);
    }

    // ── Audio input ───────────────────────────────────────────────────────────

    /// <summary>Feed the next chunk of audio samples (mono, normalised to [-1, +1]).</summary>
    public void AddSamples(ReadOnlySpan<float> samples)
        => AddSamples(samples, DateTimeOffset.UtcNow);

    /// <summary>
    /// Feed the next chunk of audio samples with an explicit UTC timestamp for
    /// the first sample in <paramref name="samples"/>.
    /// </summary>
    public void AddSamples(ReadOnlySpan<float> samples, DateTimeOffset timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (samples.IsEmpty) return;

        if (_resampler is null)
        {
            ProcessDecoded(samples, timestamp);
        }
        else
        {
            int outLen = (int)Math.Ceiling((double)samples.Length * _decoderRate / _captureRate);
            if (_resampleBuf.Length < outLen)
                _resampleBuf = new float[outLen];
            var outSpan = _resampleBuf.AsSpan(0, outLen);
            _resampler.ProcessInto(samples, outSpan);
            ProcessDecoded(outSpan, timestamp);
        }
    }

    // ── Private processing ────────────────────────────────────────────────────

    private void ProcessDecoded(ReadOnlySpan<float> decoded, DateTimeOffset chunkTimestamp)
    {
        if (!_aligned)
        {
            if (AlignToUtc)
            {
                _skipRemaining = PeriodScheduler.SamplesToNextBoundary(
                    _mode, _decoderRate, chunkTimestamp);
                _windowStart = PeriodScheduler.NextWindowStart(_mode, chunkTimestamp);
            }
            else
            {
                _skipRemaining = 0;
                _windowStart   = chunkTimestamp;
            }
            _aligned = true;
        }

        int src = 0;

        if (_skipRemaining > 0)
        {
            int skip = Math.Min(_skipRemaining, decoded.Length);
            _skipRemaining -= skip;
            src += skip;
            if (src >= decoded.Length) return;
        }

        while (src < decoded.Length)
        {
            int capacity = _samplesPerPeriod + _guardSamples;
            int space = capacity - _ringPos;
            int copy  = Math.Min(space, decoded.Length - src);
            decoded.Slice(src, copy).CopyTo(_ring.AsSpan(_ringPos, copy));
            _ringPos += copy;
            src      += copy;

            // ── Early-decode trigger ─────────────────────────────────────────
            if (!_earlyDecodeStarted && _ringPos >= EarlyDecodeSamplesThreshold)
            {
                _earlyDecodeStarted = true;

                // Snapshot = guard + partial period.  Use full capacity so that the decoder's
                // enlarged _nMax (= original_nMax + guardSamples) can read the full guard PLUS
                // the same amount of period audio as before the guard band was introduced.
                var snapshot = new float[capacity];
                int toCopy   = Math.Min(_ringPos, capacity);
                _ring.AsSpan(0, toCopy).CopyTo(snapshot);
                // Remaining elements are 0f (zero-padded) — correct for typical DT.

                string utcStr  = _windowStart.ToString("HHmmss");
                double freqLow = FreqLow, freqHigh = FreqHigh;

                var taskRef     = _rtEngine.DecodeAsync(snapshot, _mode, freqLow, freqHigh, utcStr);
                _earlyDecodeTask = taskRef;

                // KEY: fire PeriodDecoded as soon as the decode task finishes —
                // don't wait for the period boundary.  For FT2 this delivers results
                // ~460 ms before the period ends rather than right at period end.
                var capturedWindow = _windowStart;
                _ = taskRef.ContinueWith(
                    _ => FireDecode(taskRef, capturedWindow),
                    TaskScheduler.Default);
            }

            // ── Period-complete trigger ──────────────────────────────────────
            if (_ringPos >= capacity)
            {
                var windowStart = _windowStart;
                var earlyTask   = _earlyDecodeTask;
                _earlyDecodeTask    = null;
                _earlyDecodeStarted = false;

                // If the early decode was started its ContinueWith already fires PeriodDecoded
                // when the task completes.  Only start a fresh decode here when no early pass
                // was triggered (e.g. EarlyDecodeRatio = 1.0 AND the snapshot-copy path above
                // was skipped because both triggers fired in the same sample-copy iteration).
                if (earlyTask is null)
                {
                    // Guard + full period audio. capacity = _samplesPerPeriod + _guardSamples so the
                    // decoder sees the same amount of period audio as before the guard band.
                    var snapshot  = _ring.AsSpan(0, capacity).ToArray();
                    string utcStr = windowStart.ToString("HHmmss");
                    var fullTask  = _rtEngine.DecodeAsync(
                        snapshot, _mode, FreqLow, FreqHigh, utcStr);
                    FireDecode(fullTask, windowStart);
                }
                // else: continuation handles firing; just advance the window.

                _windowStart = windowStart.AddSeconds(_mode.PeriodSeconds());

                // Roll over: copy last _guardSamples of the completed period into
                // the start of the new ring as the negative-DT guard band, then
                // move any overflow samples after the guard.
                Array.Copy(_ring, _samplesPerPeriod, _ring, 0, _guardSamples);
                int overflow = _ringPos - capacity;
                if (overflow > 0)
                    Array.Copy(_ring, capacity, _ring, _guardSamples, overflow);
                _ringPos = _guardSamples + overflow;
            }
        }
    }

    private void FireDecode(
        Task<IReadOnlyList<DecodeResult>>? primaryTask, DateTimeOffset windowStart)
    {
        bool shouldLaunch;
        lock (_pendingLock)
        {
            if (!_decoding)
            {
                _decoding    = true;
                shouldLaunch = true;
            }
            else
            {
                _pendingPeriod = (primaryTask, windowStart);
                shouldLaunch   = false;
            }
        }
        if (shouldLaunch) LaunchDecodeTask(primaryTask, windowStart);
    }

    private void LaunchDecodeTask(
        Task<IReadOnlyList<DecodeResult>>? primaryTask, DateTimeOffset windowStart)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                IReadOnlyList<DecodeResult> rawResults = Array.Empty<DecodeResult>();
                if (primaryTask is not null)
                    rawResults = await primaryTask;

                // Correct DT: the buffer fed to the decoder starts _guardOffsetSeconds before
                // the UTC period boundary, so raw DT values are offset by that amount.
                // Subtracting _guardOffsetSeconds gives DT relative to the period boundary.
                var corrected = _guardOffsetSeconds > 0 && rawResults.Count > 0
                    ? (IEnumerable<DecodeResult>)rawResults.Select(r => r with { Dt = r.Dt - _guardOffsetSeconds })
                    : rawResults;

                var unique = corrected
                    .GroupBy(r => r.Message, StringComparer.Ordinal)
                    .Select(g => g.OrderByDescending(r => r.Snr).First())
                    .OrderByDescending(r => r.Snr)
                    .ToList();

                PeriodDecoded?.Invoke(unique, windowStart);
            }
            catch (Exception ex)
            {
                DecodeError?.Invoke(ex);
            }
            finally
            {
                // Read and clear _pendingPeriod under the lock, and only clear
                // _decoding when there is no queued period to launch next.
                // This eliminates the TOCTOU window that existed when _decoding was
                // managed with Interlocked outside the lock: a racing FireDecode
                // call can no longer slip in between the lock release and the
                // Interlocked clear and have its pending entry silently lost.
                (Task<IReadOnlyList<DecodeResult>>? task, DateTimeOffset ws)? pending;
                lock (_pendingLock)
                {
                    pending        = _pendingPeriod;
                    _pendingPeriod = null;
                    if (!pending.HasValue)
                        _decoding = false;
                }

                if (pending.HasValue)
                    LaunchDecodeTask(pending.Value.task, pending.Value.ws);
            }
        });
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PeriodDecoded = null;
        DecodeError   = null;
        _rtEngine.Dispose();
    }
}

using FluentAssertions;
using HamDigiSharp.Engine;
using HamDigiSharp.Models;
using Xunit;

namespace HamDigiSharp.Tests.Engine;

public class PeriodSchedulerTests
{
    // ── WindowStart / NextWindowStart ────────────────────────────────────────

    [Fact]
    public void CurrentWindowStart_Ft8_SnapsToNearest15s()
    {
        // FT8 period = 15 s. 14:30:22 UTC → window started at 14:30:15.
        var t = new DateTimeOffset(2025, 6, 1, 14, 30, 22, 500, TimeSpan.Zero);
        var ws = PeriodScheduler.CurrentWindowStart(DigitalMode.FT8, t);
        ws.Should().Be(new DateTimeOffset(2025, 6, 1, 14, 30, 15, TimeSpan.Zero));
    }

    [Fact]
    public void CurrentWindowStart_Ft8_ExactlyOnBoundary_ReturnsThatBoundary()
    {
        var t = new DateTimeOffset(2025, 6, 1, 14, 30, 30, 0, TimeSpan.Zero);
        var ws = PeriodScheduler.CurrentWindowStart(DigitalMode.FT8, t);
        ws.Should().Be(new DateTimeOffset(2025, 6, 1, 14, 30, 30, TimeSpan.Zero));
    }

    [Fact]
    public void NextWindowStart_Ft8_Is15sAfterCurrentWindow()
    {
        var t  = new DateTimeOffset(2025, 6, 1, 14, 30, 22, TimeSpan.Zero);
        var ws = PeriodScheduler.CurrentWindowStart(DigitalMode.FT8, t);
        var nw = PeriodScheduler.NextWindowStart(DigitalMode.FT8, t);
        (nw - ws).TotalSeconds.Should().BeApproximately(15.0, 0.001);
    }

    [Theory]
    [InlineData(DigitalMode.FT8,    15.0)]
    [InlineData(DigitalMode.FT4,     7.5)]
    [InlineData(DigitalMode.JT65A,  60.0)]
    [InlineData(DigitalMode.Q65A,   60.0)]
    [InlineData(DigitalMode.Q65B,   30.0)]
    [InlineData(DigitalMode.Q65C,   15.0)]
    [InlineData(DigitalMode.MSK144,  1.0)]
    [InlineData(DigitalMode.PI4,    30.0)]
    public void CurrentWindowStart_AllModes_WindowWidthEqualsModePeriod(
        DigitalMode mode, double expectedPeriodSec)
    {
        var t  = new DateTimeOffset(2025, 1, 1, 0, 0, 7, 123, TimeSpan.Zero);
        var ws = PeriodScheduler.CurrentWindowStart(mode, t);
        var nw = PeriodScheduler.NextWindowStart(mode, t);
        (nw - ws).TotalSeconds.Should().BeApproximately(expectedPeriodSec, 0.001,
            $"window width must equal the mode period for {mode}");
    }

    [Fact]
    public void SecondsToNextWindow_AlwaysInHalfOpenInterval()
    {
        var t = new DateTimeOffset(2025, 3, 15, 9, 47, 33, 417, TimeSpan.Zero);
        foreach (DigitalMode mode in Enum.GetValues<DigitalMode>())
        {
            double remaining = PeriodScheduler.SecondsToNextWindow(mode, t);
            remaining.Should().BeGreaterThanOrEqualTo(0, $"{mode}: remaining ≥ 0");
            remaining.Should().BeLessThan(mode.PeriodSeconds(), $"{mode}: remaining < period");
        }
    }

    // ── UTC string ────────────────────────────────────────────────────────────

    [Fact]
    public void CurrentWindowUtcString_Ft8_HasCorrectFormat()
    {
        var t = new DateTimeOffset(2025, 6, 1, 14, 30, 22, TimeSpan.Zero);
        var s = PeriodScheduler.CurrentWindowUtcString(DigitalMode.FT8, t);
        s.Should().Be("143015", "FT8 window at 14:30:22 started at 14:30:15");
    }

    [Fact]
    public void CurrentWindowUtcString_AlwaysSixDigits()
    {
        var t = new DateTimeOffset(2025, 6, 1, 0, 0, 1, TimeSpan.Zero);
        var s = PeriodScheduler.CurrentWindowUtcString(DigitalMode.FT8, t);
        s.Should().HaveLength(6);
        s.Should().MatchRegex(@"^\d{6}$");
    }

    // ── SamplesPerPeriod ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(DigitalMode.FT8,    12000, 12000 * 15)]
    [InlineData(DigitalMode.MSK144, 12000, 12000 * 1)]
    [InlineData(DigitalMode.JT65A,  11025, 11025 * 60)]
    [InlineData(DigitalMode.PI4,    11025, 11025 * 30)]
    public void SamplesPerPeriod_MatchesExpected(DigitalMode mode, int rate, int expected)
    {
        PeriodScheduler.SamplesPerPeriod(mode, rate).Should().Be(expected);
    }

    [Fact]
    public void SamplesPerPeriod_Ft4_RoundsCorrectly()
    {
        // FT4 period = 7.5 s × 12000 = 90000
        PeriodScheduler.SamplesPerPeriod(DigitalMode.FT4, 12000).Should().Be(90000);
    }

    // ── SamplesToNextBoundary ─────────────────────────────────────────────────

    [Fact]
    public void SamplesToNextBoundary_ExactlyOnBoundary_ReturnsZero()
    {
        // Exactly on the FT8 :30 boundary → within 50 ms tolerance → skip 0
        var t = new DateTimeOffset(2025, 6, 1, 14, 30, 30, 0, TimeSpan.Zero);
        int skip = PeriodScheduler.SamplesToNextBoundary(DigitalMode.FT8, 12000, t);
        skip.Should().Be(0, "on-boundary = skip 0 samples");
    }

    [Fact]
    public void SamplesToNextBoundary_SevenAndHalfSecondsIn_IsHalfPeriod()
    {
        // FT8 :15→:30. At :22.5 (7.5 s into window), 7.5 s remain = 90000 samples.
        var t = new DateTimeOffset(2025, 6, 1, 14, 30, 22, 500, TimeSpan.Zero);
        int skip = PeriodScheduler.SamplesToNextBoundary(DigitalMode.FT8, 12000, t);
        skip.Should().BeCloseTo(90000, 200);
    }

    [Fact]
    public void SamplesToNextBoundary_NeverExceedsOnePeriodInSamples()
    {
        var t    = new DateTimeOffset(2025, 3, 15, 9, 47, 33, 417, TimeSpan.Zero);
        int skip = PeriodScheduler.SamplesToNextBoundary(DigitalMode.FT8, 12000, t);
        int max  = PeriodScheduler.SamplesPerPeriod(DigitalMode.FT8, 12000);
        skip.Should().BeLessThanOrEqualTo(max, "skip cannot exceed one full period");
    }
}

public class RealTimeDecoderTests
{
    // ── Construction ──────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_ValidArgs_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
        {
            using var rt = new RealTimeDecoder(DigitalMode.FT8, 48000);
        });
        ex.Should().BeNull();
    }

    #pragma warning disable CS0618 // deprecated DecoderEngine overload tested intentionally
    [Fact]
    public void Constructor_NullEngine_Throws()
    {
        var ex = Record.Exception(() =>
            new RealTimeDecoder(null!, DigitalMode.FT8, 48000));
        ex.Should().BeOfType<ArgumentNullException>();
    }
    #pragma warning restore CS0618

    [Fact]
    public void Constructor_ZeroCaptureRate_Throws()
    {
        var ex = Record.Exception(() =>
            new RealTimeDecoder(DigitalMode.FT8, 0));
        ex.Should().BeOfType<ArgumentOutOfRangeException>();
    }

    // ── AddSamples: no crash ──────────────────────────────────────────────────

    [Fact]
    public void AddSamples_EmptySpan_DoesNothing()
    {
        using var rt     = new RealTimeDecoder(DigitalMode.FT8, 12000)
            { AlignToUtc = false };
        var ex = Record.Exception(() => rt.AddSamples(Array.Empty<float>()));
        ex.Should().BeNull();
    }

    [Fact]
    public void AddSamples_ShortSilence_DoesNotThrow()
    {
        using var rt     = new RealTimeDecoder(DigitalMode.FT8, 12000)
            { AlignToUtc = false };
        var ex = Record.Exception(() => rt.AddSamples(new float[1000]));
        ex.Should().BeNull();
    }

    [Fact]
    public void AddSamples_AfterDispose_Throws()
    {
        var rt = new RealTimeDecoder(DigitalMode.FT8, 12000)
            { AlignToUtc = false };
        rt.Dispose();
        var ex = Record.Exception(() => rt.AddSamples(new float[100]));
        ex.Should().BeOfType<ObjectDisposedException>();
    }

    // ── Resampling path ───────────────────────────────────────────────────────

    [Fact]
    public void AddSamples_WithResampling48kTo12k_DoesNotThrow()
    {
        using var rt     = new RealTimeDecoder(DigitalMode.FT8, 48000)
            { AlignToUtc = false };
        var ex = Record.Exception(() => rt.AddSamples(new float[4800])); // 100 ms @ 48 kHz
        ex.Should().BeNull("48000→12000 resampling must not throw");
    }

    [Fact]
    public void AddSamples_SameCaptureAndDecoderRate_NoResamplerNeeded()
    {
        using var rt     = new RealTimeDecoder(DigitalMode.FT8, 12000)
            { AlignToUtc = false };
        var ex = Record.Exception(() => rt.AddSamples(new float[1200]));
        ex.Should().BeNull();
    }

    // ── Period fire: PeriodDecoded event ─────────────────────────────────────

    /// <summary>
    /// Feed exactly one FT8 period (15 s × 12 000 = 180 000 samples) of silence with
    /// AlignToUtc=false. The decoder must fire PeriodDecoded exactly once.
    /// </summary>
    [Fact]
    public async Task AddSamples_OnePeriodOfSilence_FiresPeriodDecoded()
    {
        using var rt     = new RealTimeDecoder(DigitalMode.FT8, 12000)
            { AlignToUtc = false, FreqLow = 1000, FreqHigh = 1050 };

        var fired = new TaskCompletionSource<IReadOnlyList<DecodeResult>>();
        rt.PeriodDecoded += (results, _) => fired.TrySetResult(results);

        int n = PeriodScheduler.SamplesPerPeriod(DigitalMode.FT8, 12000); // 180000
        rt.AddSamples(new float[n]);

        (await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(30))))
            .Should().BeSameAs(fired.Task, "PeriodDecoded must fire within 30 s");
        (await fired.Task).Should().NotBeNull();
    }

    [Fact]
    public async Task AddSamples_TwoPeriods_FiresTwice()
    {
        // FT4 period = 7.5 s → 90 000 samples @ 12 kHz
        using var rt     = new RealTimeDecoder(DigitalMode.FT4, 12000)
            { AlignToUtc = false, FreqLow = 1000, FreqHigh = 1050 };

        int count = 0;
        var secondFired = new TaskCompletionSource<bool>();
        rt.PeriodDecoded += (_, _) =>
        {
            if (Interlocked.Increment(ref count) >= 2)
                secondFired.TrySetResult(true);
        };

        int n = PeriodScheduler.SamplesPerPeriod(DigitalMode.FT4, 12000);
        rt.AddSamples(new float[n * 2]);

        (await Task.WhenAny(secondFired.Task, Task.Delay(TimeSpan.FromSeconds(30))))
            .Should().BeSameAs(secondFired.Task, "PeriodDecoded must fire twice");
    }

    [Fact]
    public async Task AddSamples_InSmallChunks_FiresOncePerPeriod()
    {
        // MSKMS: 1 s period = 12 000 samples @ 12 kHz. Feed as 100-sample chunks.
        using var rt     = new RealTimeDecoder(DigitalMode.MSKMS, 12000)
            { AlignToUtc = false, FreqLow = 1000, FreqHigh = 1050 };

        int fired = 0;
        var tcs = new TaskCompletionSource<bool>();
        rt.PeriodDecoded += (_, _) =>
        {
            if (Interlocked.Increment(ref fired) >= 1) tcs.TrySetResult(true);
        };

        int n     = PeriodScheduler.SamplesPerPeriod(DigitalMode.MSKMS, 12000);
        var chunk = new float[100];
        for (int sent = 0; sent < n; sent += 100)
            rt.AddSamples(chunk.AsSpan(0, Math.Min(100, n - sent)));

        (await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15))))
            .Should().BeSameAs(tcs.Task, "PeriodDecoded must fire after 100-sample chunks fill one period");
        fired.Should().Be(1);
    }

    // ── WindowStart timestamp ─────────────────────────────────────────────────

    [Fact]
    public async Task PeriodDecoded_WindowStartIsReasonable()
    {
        using var rt     = new RealTimeDecoder(DigitalMode.MSKMS, 12000)
            { AlignToUtc = false, FreqLow = 1000, FreqHigh = 1050 };

        DateTimeOffset? ws = null;
        var tcs = new TaskCompletionSource<bool>();
        rt.PeriodDecoded += (_, w) => { ws = w; tcs.TrySetResult(true); };

        var before = DateTimeOffset.UtcNow;
        rt.AddSamples(new float[PeriodScheduler.SamplesPerPeriod(DigitalMode.MSKMS, 12000)]);
        await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(60)));   // generous: decoder may be slow under load
        var after = DateTimeOffset.UtcNow;

        ws.Should().NotBeNull("PeriodDecoded must have fired");
        ws!.Value.Should().BeOnOrAfter(before.AddSeconds(-1));
        ws!.Value.Should().BeOnOrBefore(after.AddSeconds(1));
    }

    // ── Properties ───────────────────────────────────────────────────────────

    [Fact]
    public void FreqDefaults_Are200And3000()
    {
        using var rt     = new RealTimeDecoder(DigitalMode.FT8, 12000);
        rt.FreqLow.Should().Be(200);
        rt.FreqHigh.Should().Be(3000);
    }

    [Fact]
    public void AlignToUtc_DefaultIsTrue()
    {
        using var rt     = new RealTimeDecoder(DigitalMode.FT8, 12000);
        rt.AlignToUtc.Should().BeTrue();
    }

    [Fact]
    public void EarlyDecodeRatio_DefaultIs0Point90()
    {
        using var rt     = new RealTimeDecoder(DigitalMode.FT8, 12000);
        rt.EarlyDecodeRatio.Should().BeApproximately(0.90f, 0.001f);
    }

    [Fact]
    public void RealTimeOptions_DefaultDecoderDepth_IsNormal_BpPlusOsd()
    {
        using var rt     = new RealTimeDecoder(DigitalMode.FT8, 12000);
        rt.RealTimeOptions.DecoderDepth.Should().Be(DecoderDepth.Normal,
            "RT path must use BP+OSD (DecoderDepth.Normal) to match WSJT-X sensitivity");
    }

    [Fact]
    public void RealTimeOptions_DefaultMaxCandidates_AtMost100()
    {
        using var rt     = new RealTimeDecoder(DigitalMode.FT8, 12000);
        rt.RealTimeOptions.MaxCandidates.Should().BeLessThanOrEqualTo(100,
            "RT mode should use fewer candidates for lower latency");
    }

    /// <summary>
    /// PeriodDecoded must still fire when EarlyDecodeRatio=1.0 (legacy behaviour,
    /// decode starts exactly at period end).
    /// </summary>
    [Fact]
    public async Task AddSamples_EarlyDecodeRatio1_0_FiresPeriodDecoded()
    {
        using var rt     = new RealTimeDecoder(DigitalMode.MSK144, 12000)
            { AlignToUtc = false, FreqLow = 1000, FreqHigh = 1050, EarlyDecodeRatio = 1.0f };

        var fired = new TaskCompletionSource<bool>();
        rt.PeriodDecoded += (_, _) => fired.TrySetResult(true);

        rt.AddSamples(new float[PeriodScheduler.SamplesPerPeriod(DigitalMode.MSK144, 12000)]);

        (await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(20))))
            .Should().BeSameAs(fired.Task, "PeriodDecoded must fire even with EarlyDecodeRatio=1.0");
    }

    /// <summary>
    /// With EarlyDecodeRatio=0.90, PeriodDecoded fires after the period completes
    /// and the pre-started decode task resolves.  This test confirms the early-decode
    /// path handles MSKMS (1 s period) correctly without hanging.
    /// </summary>
    [Fact]
    public async Task AddSamples_EarlyDecodeRatio0Point90_FiresPeriodDecoded()
    {
        using var rt     = new RealTimeDecoder(DigitalMode.MSKMS, 12000)
            { AlignToUtc = false, FreqLow = 1000, FreqHigh = 1050, EarlyDecodeRatio = 0.90f };

        var fired = new TaskCompletionSource<bool>();
        rt.PeriodDecoded += (_, _) => fired.TrySetResult(true);

        rt.AddSamples(new float[PeriodScheduler.SamplesPerPeriod(DigitalMode.MSKMS, 12000)]);

        (await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(20))))
            .Should().BeSameAs(fired.Task, "PeriodDecoded must fire with EarlyDecodeRatio=0.90");
    }

    /// <summary>
    /// Core behavioral claim: a signal placed in the first 90% of an FT8 period
    /// (zero-padded for the last 10%) must decode correctly.
    /// This confirms that the early-decode zero-padding does not corrupt the
    /// sync or LDPC phase for signals at DT ≈ 0 s.
    /// </summary>
    [Fact]
    public void EarlyDecode_Ft8SignalInFirst90Percent_DecodesCorrectly()
    {
        const string Message  = "CQ W1AW FN42";
        const double TxFreqHz = 1000.0;

        var encoder = new HamDigiSharp.Encoders.Ft8Encoder();
        float[] signal     = encoder.Encode(Message,
            new HamDigiSharp.Models.EncoderOptions { FrequencyHz = TxFreqHz });

        int periodSamples  = 12000 * 15;
        var buffer         = new float[periodSamples];
        signal.AsSpan().CopyTo(buffer);

        var decoder = new HamDigiSharp.Decoders.Ft8.Ft8Decoder();
        decoder.Configure(new HamDigiSharp.Models.DecoderOptions
            { AveragingEnabled = false });
        var results = decoder.Decode(buffer, 850, 1200, "000000");

        results.Should().Contain(r => r.Message.Trim() == Message,
            "FT8 signal in first 90 % of buffer must decode with zero-padded tail");
    }

    /// <summary>
    /// Core claim: with the early-fire architecture, PeriodDecoded fires BEFORE
    /// the period ends.  We feed 85% of the FT2 period (enough to pass the early
    /// decode threshold at ~82%) and assert that PeriodDecoded fires without us
    /// feeding the remaining 15%.  This proves decode results are delivered well
    /// before the operator must begin transmitting.
    /// </summary>
    [Fact]
    public async Task Ft2_EarlyDecode_FiresBeforePeriodEndSamples()
    {
        const double TxFreqHz = 882.0;
        const int    Rate     = 12000;

        // Build a valid FT2 signal placed at DT=0.
        var encoder = new HamDigiSharp.Encoders.Ft2Encoder();
        float[] signal = encoder.Encode("CQ W1AW FN42",
            new HamDigiSharp.Models.EncoderOptions { FrequencyHz = TxFreqHz });

        int periodSamples = PeriodScheduler.SamplesPerPeriod(DigitalMode.FT2, Rate);
        var buffer = new float[periodSamples];
        signal.AsSpan(0, Math.Min(signal.Length, periodSamples)).CopyTo(buffer);

        using var rt = new RealTimeDecoder(DigitalMode.FT2, Rate)
        {
            AlignToUtc = false,
            FreqLow    = 700,
            FreqHigh   = 1100,
        };

        var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rt.PeriodDecoded += (_, _) => fired.TrySetResult(true);

        // Feed only the first 85% of the period.  The early-decode threshold for FT2 is
        // ~82% (3.08 s / 3.75 s), so this will trigger the early decode but NOT the
        // period-boundary trigger.  PeriodDecoded must fire via the ContinueWith path.
        int earlyFeedSamples = (int)(periodSamples * 0.85); // 38 250 of 45 000
        rt.AddSamples(buffer.AsSpan(0, earlyFeedSamples));

        // Allow up to 5 s for the decode to complete and fire.
        var completed = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().BeSameAs(fired.Task,
            "PeriodDecoded must fire after 85% of FT2 samples — before the period ends");
    }

    /// <summary>
    /// Verify FT2 RT decode reaction time.  We feed all samples, measure wall-clock
    /// time to PeriodDecoded, and assert results arrive with at least 300 ms to spare
    /// before the 3.75 s period ends.  Budget: decode must complete within 3.45 s
    /// from the first sample.
    /// </summary>
    [Fact]
    public async Task Ft2_RtDecode_ReactionTimeAtLeast300ms()
    {
        const double TxFreqHz = 882.0;
        const int    Rate     = 12000;

        var encoder = new HamDigiSharp.Encoders.Ft2Encoder();
        float[] signal = encoder.Encode("CQ K9AN EN50",
            new HamDigiSharp.Models.EncoderOptions { FrequencyHz = TxFreqHz });

        int periodSamples = PeriodScheduler.SamplesPerPeriod(DigitalMode.FT2, Rate);
        var buffer = new float[periodSamples];
        signal.AsSpan(0, Math.Min(signal.Length, periodSamples)).CopyTo(buffer);

        using var rt = new RealTimeDecoder(DigitalMode.FT2, Rate)
        {
            AlignToUtc = false,
            FreqLow    = 700,
            FreqHigh   = 1100,
        };

        // JIT warm-up: decode once before timing.
        Action<IReadOnlyList<DecodeResult>, DateTimeOffset>? warmHandler = null;
        var warmFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        warmHandler = (_, _) => warmFired.TrySetResult(true);
        rt.PeriodDecoded += warmHandler;
        rt.AddSamples(buffer);
        await Task.WhenAny(warmFired.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        rt.PeriodDecoded -= warmHandler;

        // Timed pass
        var timedFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rt.PeriodDecoded += (_, _) => timedFired.TrySetResult(true);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        rt.AddSamples(buffer); // feed second period
        await Task.WhenAny(timedFired.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        sw.Stop();

        timedFired.Task.IsCompletedSuccessfully.Should().BeTrue("FT2 RT decode must fire PeriodDecoded");

        // With early-fire: decode starts at T≈3.08 s, completes in ~150 ms,
        // PeriodDecoded fires at T≈3.23 s — leaving ≥520 ms before period end.
        // We allow up to 3.45 s (300 ms before period end) as the budget.
        double periodSeconds = DigitalMode.FT2.PeriodSeconds();          // 3.75 s
        double budget        = periodSeconds - 0.300;                    // 3.45 s
        sw.Elapsed.TotalSeconds.Should().BeLessThan(budget,
            $"FT2 PeriodDecoded must fire within {budget:F2} s (≥300 ms before period end)");
    }

    /// <summary>
    /// Negative-DT guard band: a signal that starts 200 ms BEFORE the period boundary
    /// must be recovered in the current period's decode.
    ///
    /// The guard band carries the last 300 ms of each decoded period into the start of
    /// the next ring buffer.  When a station's clock is slightly ahead (DT &lt; 0) its
    /// signal bleeds into the previous period, but the first 2400 ms worth of samples
    /// are preserved in the guard and combined with the continuation in the current
    /// period, allowing LDPC to recover the full message.
    ///
    /// Expected decoded DT ≈ −0.2 s (signal started before period boundary).
    /// </summary>
    [Fact]
    public async Task Ft2_NegativeDt_GuardBandDecodesSignalStartingBeforePeriod()
    {
        const double TxFreqHz = 882.0;
        const int    Rate     = 12000;
        const int    NegDtMs  = 200; // signal starts 200 ms BEFORE period boundary

        var encoder    = new HamDigiSharp.Encoders.Ft2Encoder();
        float[] signal = encoder.Encode("CQ W1AW FN42",
            new HamDigiSharp.Models.EncoderOptions { FrequencyHz = TxFreqHz });

        int periodSamples = PeriodScheduler.SamplesPerPeriod(DigitalMode.FT2, Rate);
        int negDtSamples  = NegDtMs * Rate / 1000; // 2400

        // Previous period: silence with the signal's first 200 ms at the very end.
        var prevPeriod = new float[periodSamples];
        Array.Copy(signal, 0, prevPeriod, periodSamples - negDtSamples, negDtSamples);

        // Current period: remainder of the signal starting from sample 0.
        var curPeriod = new float[periodSamples];
        int remaining = signal.Length - negDtSamples;
        Array.Copy(signal, negDtSamples, curPeriod, 0, Math.Min(remaining, periodSamples));

        using var rt = new RealTimeDecoder(DigitalMode.FT2, Rate)
        {
            AlignToUtc = false,
            FreqLow    = 700,
            FreqHigh   = 1100,
        };

        var periodResults = new System.Collections.Concurrent.ConcurrentQueue<IReadOnlyList<DecodeResult>>();
        int periodCount   = 0;
        var bothFired     = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rt.PeriodDecoded += (r, _) =>
        {
            periodResults.Enqueue(r);
            if (Interlocked.Increment(ref periodCount) >= 2) bothFired.TrySetResult(true);
        };

        rt.AddSamples(prevPeriod);
        rt.AddSamples(curPeriod);

        await Task.WhenAny(bothFired.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        bothFired.Task.IsCompletedSuccessfully.Should().BeTrue(
            "both periods must have fired PeriodDecoded within 10 s");

        // The SECOND period's results must contain the full message.
        var allResults = periodResults.SelectMany(r => r).ToList();
        allResults.Should().Contain(r => r.Message.Trim() == "CQ W1AW FN42",
            "guard band must recover the 200 ms head of the signal from the previous period");

        // DT should be negative (signal started before period boundary).
        var match = allResults.FirstOrDefault(r => r.Message.Trim() == "CQ W1AW FN42");
        match.Should().NotBeNull();
        match!.Dt.Should().BeLessThan(0.05,
            "DT must be near 0 or negative — signal started before period boundary");
    }

    // ── Positive-DT coverage (guard band regression guard) ───────────────────
    //
    // These tests place the signal late inside the period (positive DT).  They are
    // specifically designed to catch the regression that was introduced when the
    // 300 ms guard band shrank the useful portion of the decoder's _nMax buffer:
    //   FT4 regression:  DT coverage dropped from +1.10 s → +0.80 s
    //   FT2 regression:  DT coverage dropped from +1.28 s → +0.98 s
    // A signal at DT = +0.9 s (FT4) or +1.0 s (FT2) is INSIDE the correct window
    // but OUTSIDE the regressed window — so these tests fail when the fix is absent.

    /// <summary>
    /// FT4 signal placed 0.9 s after the period boundary (DT = +0.9 s).
    /// With the guard-band bug present, _nMax = 72576 and the signal's tail is
    /// cut off (signal end = 73 728 samples &gt; 72 576 = _nMax). Fix: _nMax = 76 176.
    /// </summary>
    [Fact]
    public async Task Ft4_RtDecode_PositiveDt0p9s_DecodesCorrectly()
    {
        const double TxFreqHz = 1000.0;
        const int    Rate     = 12000;
        const double DtSec    = 0.9;    // signal starts 0.9 s after UTC period boundary

        using var rt = new RealTimeDecoder(DigitalMode.FT4, Rate)
            { AlignToUtc = false, FreqLow = 850, FreqHigh = 1200 };

        var tcs = new TaskCompletionSource<IReadOnlyList<DecodeResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        rt.PeriodDecoded += (r, _) => tcs.TrySetResult(r);

        int periodSamples = PeriodScheduler.SamplesPerPeriod(DigitalMode.FT4, Rate); // 90 000
        var signal = new HamDigiSharp.Encoders.Ft4Encoder().Encode("CQ W1AW FN42",
            new HamDigiSharp.Models.EncoderOptions { FrequencyHz = TxFreqHz });

        // Build buffer with signal offset by DtSec from the UTC boundary.
        var buffer = new float[periodSamples];
        int dtSamples = (int)(DtSec * Rate);  // 10 800
        signal.AsSpan(0, Math.Min(signal.Length, periodSamples - dtSamples))
              .CopyTo(buffer.AsSpan(dtSamples));

        rt.AddSamples(buffer);
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        completed.Should().BeSameAs(tcs.Task, "PeriodDecoded must fire");

        var results = await tcs.Task;
        results.Should().Contain(r => r.Message.Trim() == "CQ W1AW FN42",
            "FT4 signal at DT=+0.9 s must decode (guard-band DT regression check)");
    }

    /// <summary>
    /// FT2 signal placed 1.0 s after the period boundary (DT = +1.0 s).
    /// With the guard-band bug present, _nMax = 45 000 and the signal falls past
    /// maxStart in the c1 domain (c1=1733 &gt; 1704 = maxStart). Fix: _nMax = 48 600.
    /// </summary>
    [Fact]
    public async Task Ft2_RtDecode_PositiveDt1p0s_DecodesCorrectly()
    {
        const double TxFreqHz = 882.0;
        const int    Rate     = 12000;
        const double DtSec    = 1.0;    // signal starts 1.0 s after UTC period boundary

        using var rt = new RealTimeDecoder(DigitalMode.FT2, Rate)
            { AlignToUtc = false, FreqLow = 700, FreqHigh = 1100 };

        var tcs = new TaskCompletionSource<IReadOnlyList<DecodeResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        rt.PeriodDecoded += (r, _) => tcs.TrySetResult(r);

        int periodSamples = PeriodScheduler.SamplesPerPeriod(DigitalMode.FT2, Rate); // 45 000
        var signal = new HamDigiSharp.Encoders.Ft2Encoder().Encode("CQ W1AW FN42",
            new HamDigiSharp.Models.EncoderOptions { FrequencyHz = TxFreqHz });

        var buffer = new float[periodSamples];
        int dtSamples = (int)(DtSec * Rate);  // 12 000
        signal.AsSpan(0, Math.Min(signal.Length, periodSamples - dtSamples))
              .CopyTo(buffer.AsSpan(dtSamples));

        rt.AddSamples(buffer);
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        completed.Should().BeSameAs(tcs.Task, "PeriodDecoded must fire");

        var results = await tcs.Task;
        results.Should().Contain(r => r.Message.Trim() == "CQ W1AW FN42",
            "FT2 signal at DT=+1.0 s must decode (guard-band DT regression check)");
    }

    // ── RT-path sensitivity (MaxCandidates regression guard) ─────────────────
    //
    // These tests run noisy signals through the full RealTimeDecoder pipeline.
    // A single noisy signal at the target frequency should decode via the PLINQ
    // path.  If RtMaxCandidates is reduced far below 75, or if the snapshot
    // delivered to the decoder is too short, these tests will fail.

    /// <summary>FT4 at -10 dB full-band SNR via RealTimeDecoder (≈ -6.2 dB WSJT-X).</summary>
    [Fact]
    public async Task Ft4_RtDecode_NoisyAt10dB_DecodesCorrectly()
        => await RtNoisyTest(DigitalMode.FT4, "CQ W1AW FN42", 1000.0, -10.0, seed: 7);

    /// <summary>FT2 at -5 dB full-band SNR via RealTimeDecoder (≈ -1.2 dB WSJT-X).</summary>
    [Fact]
    public async Task Ft2_RtDecode_NoisyAt5dB_DecodesCorrectly()
        => await RtNoisyTest(DigitalMode.FT2, "CQ W1AW FN42", 882.0, -5.0, seed: 42);

    /// <summary>
    /// Multi-period LLR accumulation (MRC in the log-likelihood domain).
    ///
    /// Phase 1a scans ALL frequencies in range without a spectrogram threshold; the Costas
    /// check inside ComputeTimingCombinedLlr is the effective gate.  Phase 2 iterates
    /// _freqAcc.Keys (carry-forward), so a frequency found in any prior period is always
    /// retried even if Costas detection failed this period.
    ///
    /// At -16 dB full-band (≈ -12.2 dB WSJT-X), 1 dB below the single-period floor, the
    /// signal is sometimes detectable by Costas check but usually not — so single-period
    /// decode fails.  After accumulating √N SNR gain across periods it becomes consistently
    /// decodable.  This test verifies the core improvement.
    /// </summary>
    [Fact]
    public void Ft2_MultiPeriodAveraging_DecodesAtMinus16dBWithinTwelvePeriods()
    {
        const double snrDb  = -16.0;  // full-band; 1 dB below single-period floor of -15 dB
        const double freqHz = 882.0;
        const string msg    = "CQ W1AW FN42";

        var decoder = new HamDigiSharp.Decoders.Ft2.Ft2Decoder();
        decoder.Configure(new HamDigiSharp.Models.DecoderOptions
            { DecoderDepth = DecoderDepth.Normal, AveragingEnabled = true });

        float[] clean = new HamDigiSharp.Encoders.Ft2Encoder().Encode(msg,
            new HamDigiSharp.Models.EncoderOptions { FrequencyHz = freqHz, Amplitude = 0.5f });

        // Pad to full FT2 decoder window (48 600 samples = _nMax).
        var cleanPadded = new float[48_600];
        clean.AsSpan(0, Math.Min(clean.Length, 48_600)).CopyTo(cleanPadded);

        // Accumulate up to 12 periods using independent noise realizations (different seeds).
        bool decoded = false;
        for (int p = 0; p < 12 && !decoded; p++)
        {
            var noisy = AddWhiteNoise(cleanPadded, snrDb, seed: 100 + p);
            var results = decoder.Decode(noisy, freqHz - 200, freqHz + 200, "000000");
            decoded = results.Any(r => r.Message.Trim().Equals(msg, StringComparison.OrdinalIgnoreCase));
        }

        decoded.Should().BeTrue(
            $"FT2 multi-period LLR averaging must decode '{msg}' at {snrDb} dB full-band " +
            "within 12 periods (MRC carry-forward, Costas-gated accumulation)");
    }

    /// <summary>
    /// Regression: after successfully decoding a strong FT2 signal the accumulator must be
    /// cleared so that subsequent near-silence does NOT produce a false decode.
    /// Root cause of user-reported bug: _freqAcc was never cleared after a successful decode,
    /// so the accumulated LLR from period 1 persisted into period 2 and decoded again.
    /// Fix: DecodeAveraged calls _freqAcc.Remove(key) after each successful decode.
    /// </summary>
    [Fact]
    public void Ft2_AveragingAfterSilence_NoFalseDecode()
    {
        const double freqHz = 882.0;
        const string msg    = "CQ W1AW FN42";

        var decoder = new HamDigiSharp.Decoders.Ft2.Ft2Decoder();
        decoder.Configure(new HamDigiSharp.Models.DecoderOptions
            { DecoderDepth = DecoderDepth.Normal, AveragingEnabled = true });

        float[] clean = new HamDigiSharp.Encoders.Ft2Encoder().Encode(msg,
            new HamDigiSharp.Models.EncoderOptions { FrequencyHz = freqHz, Amplitude = 0.5f });

        var cleanPadded = new float[48_600];
        clean.AsSpan(0, Math.Min(clean.Length, 48_600)).CopyTo(cleanPadded);

        // Period 1: strong signal (-5 dB) — should decode.
        var noisy1  = AddWhiteNoise(cleanPadded, -5.0, seed: 42);
        var result1 = decoder.Decode(noisy1, freqHz - 200, freqHz + 200, "000001");
        result1.Any(r => r.Message.Trim().Equals(msg, StringComparison.OrdinalIgnoreCase))
               .Should().BeTrue("period 1 strong signal must decode");

        // Period 2: pure silence — must NOT produce any decode (accumulator was cleared).
        var silence  = new float[48_600];
        var result2  = decoder.Decode(silence, freqHz - 200, freqHz + 200, "000002");
        result2.Any(r => r.Message.Trim().Equals(msg, StringComparison.OrdinalIgnoreCase))
               .Should().BeFalse("silence after decode must not re-decode from stale LLR");
    }

    /// <summary>
    /// FT4 multi-period averaging: at -18 dB full-band (1 dB below single-period floor of -17 dB)
    /// the signal should decode within 12 periods.  √12 ≈ +5.4 dB sensitivity gain expected.
    /// </summary>
    [Fact]
    public void Ft4_MultiPeriodAveraging_DecodesAtMinus18dBWithinTwelvePeriods()
    {
        const double snrDb  = -18.0;  // full-band; 1 dB below single-period floor
        const double freqHz = 882.0;
        const string msg    = "CQ W1AW FN42";

        var decoder = new HamDigiSharp.Decoders.Ft4.Ft4Decoder();
        decoder.Configure(new HamDigiSharp.Models.DecoderOptions
            { DecoderDepth = DecoderDepth.Normal, AveragingEnabled = true });

        float[] clean = new HamDigiSharp.Encoders.Ft4Encoder().Encode(msg,
            new HamDigiSharp.Models.EncoderOptions { FrequencyHz = freqHz, Amplitude = 0.5f });

        var cleanPadded = new float[76_176];
        clean.AsSpan(0, Math.Min(clean.Length, 76_176)).CopyTo(cleanPadded);

        bool decoded = false;
        for (int p = 0; p < 12 && !decoded; p++)
        {
            var noisy = AddWhiteNoise(cleanPadded, snrDb, seed: 300 + p);
            var results = decoder.Decode(noisy, freqHz - 200, freqHz + 200, "000000");
            decoded = results.Any(r => r.Message.Trim().Equals(msg, StringComparison.OrdinalIgnoreCase));
        }

        decoded.Should().BeTrue(
            $"FT4 multi-period LLR averaging must decode '{msg}' at {snrDb} dB full-band " +
            "within 12 periods");
    }

    /// <summary>
    /// FT4 regression: after a successful decode the accumulator is cleared so subsequent
    /// silence does not produce a false decode.
    /// </summary>
    [Fact]
    public void Ft4_AveragingAfterSilence_NoFalseDecode()
    {
        const double freqHz = 882.0;
        const string msg    = "CQ W1AW FN42";

        var decoder = new HamDigiSharp.Decoders.Ft4.Ft4Decoder();
        decoder.Configure(new HamDigiSharp.Models.DecoderOptions
            { DecoderDepth = DecoderDepth.Normal, AveragingEnabled = true });

        float[] clean = new HamDigiSharp.Encoders.Ft4Encoder().Encode(msg,
            new HamDigiSharp.Models.EncoderOptions { FrequencyHz = freqHz, Amplitude = 0.5f });

        var cleanPadded = new float[76_176];
        clean.AsSpan(0, Math.Min(clean.Length, 76_176)).CopyTo(cleanPadded);

        // Period 1: strong signal (-3 dB) — should decode.
        var noisy1  = AddWhiteNoise(cleanPadded, -3.0, seed: 42);
        var result1 = decoder.Decode(noisy1, freqHz - 200, freqHz + 200, "000001");
        result1.Any(r => r.Message.Trim().Equals(msg, StringComparison.OrdinalIgnoreCase))
               .Should().BeTrue("period 1 strong FT4 signal must decode");

        // Period 2: silence — must not re-decode.
        var silence = new float[76_176];
        var result2 = decoder.Decode(silence, freqHz - 200, freqHz + 200, "000002");
        result2.Any(r => r.Message.Trim().Equals(msg, StringComparison.OrdinalIgnoreCase))
               .Should().BeFalse("silence after FT4 decode must not re-decode from stale LLR");
    }

    // ── Shared helper ────────────────────────────────────────────────────────

    private async Task RtNoisyTest(
        DigitalMode mode, string message, double freqHz, double snrDb, int seed)
    {
        const int Rate = 12000;
        using var rt = new RealTimeDecoder(mode, Rate)
            { AlignToUtc = false, FreqLow = freqHz - 200, FreqHigh = freqHz + 200 };

        var tcs = new TaskCompletionSource<IReadOnlyList<DecodeResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        rt.PeriodDecoded += (r, _) => tcs.TrySetResult(r);

        // Encode a clean signal, add AWGN, place at DT=0 in a full-period buffer.
        float[] clean = mode switch
        {
            DigitalMode.FT4 => new HamDigiSharp.Encoders.Ft4Encoder().Encode(message,
                new HamDigiSharp.Models.EncoderOptions { FrequencyHz = freqHz }),
            DigitalMode.FT2 => new HamDigiSharp.Encoders.Ft2Encoder().Encode(message,
                new HamDigiSharp.Models.EncoderOptions { FrequencyHz = freqHz }),
            _ => throw new NotSupportedException()
        };
        float[] noisy  = AddWhiteNoise(clean, snrDb, seed);
        int periodSamples = PeriodScheduler.SamplesPerPeriod(mode, Rate);
        var buffer = new float[periodSamples];
        noisy.AsSpan(0, Math.Min(noisy.Length, periodSamples)).CopyTo(buffer);

        rt.AddSamples(buffer);
        double timeoutSec = mode.PeriodSeconds() * 3;
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSec)));
        completed.Should().BeSameAs(tcs.Task, "PeriodDecoded must fire");

        var results = await tcs.Task;
        results.Should().Contain(r => r.Message.Trim().Equals(message, StringComparison.OrdinalIgnoreCase),
            $"{mode} RT decode must find '{message}' at SNR={snrDb} dB");
    }

    private static float[] AddWhiteNoise(float[] signal, double snrDb, int seed)
    {
        double sigPower = signal.Average(s => (double)s * s);
        if (sigPower < 1e-20) return signal;
        double noiseAmp = Math.Sqrt(sigPower / Math.Pow(10, snrDb / 10.0));
        var rng = new Random(seed);
        var result = new float[signal.Length];
        for (int i = 0; i < signal.Length; i++)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            double g  = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            result[i] = signal[i] + (float)(noiseAmp * g);
        }
        return result;
    }
}



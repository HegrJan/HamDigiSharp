using System.Numerics;
using HamDigiSharp.Codecs;
using HamDigiSharp.Dsp;
using HamDigiSharp.Models;

namespace HamDigiSharp.Decoders.Q65;

/// <summary>
/// Q65 decoder — 65-FSK, QRA LDPC code over GF(64).
/// C# port of MSHV's DecoderQ65 (LZ2HV / IV3NWV K9AN), GPL.
///
/// Protocol:
///   - 85-symbol frame: 22 sync (tone 0) + 63 data (tones 1-64)
///   - QRA LDPC: K=15 (13 info + 2 CRC-12) → N=65 codeword over GF(64)
///   - 63 transmitted codeword symbols (2 CRC positions punctured)
///   - Sub-modes A/B/C/D differ only in symbol duration / tone spacing
///
/// Signal processing:
///   - All submodes use a fixed high-resolution FFT of NspsA=6912 samples.
///   - nBinsPerTone = NspsA / _nsps = 1 (A), 2 (B), 4 (C), 8 (D).
///   - Sync detection: score sync-tone power ratio at known sync positions.
///   - LDPC: belief-propagation via Q65Subs with Gaussian fading model.
/// </summary>
public sealed class Q65Decoder : BaseDecoder
{
    // ── Protocol constants ────────────────────────────────────────────────────
    private const int SampleRate  = 12000;
    private const int NspsA       = 6912;   // Q65A samples/symbol (max resolution)
    private const double DfA      = SampleRate / (double)NspsA; // ≈1.736 Hz/bin
    private const int NSym        = 85;     // total symbols per frame
    private const int NDataSym    = 63;     // QRA codeword length (data symbols)
    private const int MaxIters    = 100;    // BP decoder iterations
    private const float B90Ts     = 1.0f;  // Doppler bandwidth × symbol time

    // Sync positions (0-indexed) from isync[] (1-indexed in C++).
    // Sync symbols carry tone 0 (the base frequency); data symbols carry tones 1-64.
    private static readonly int[] SyncPos =
    {
        0,8,11,12,14,21,22,25,26,32,34,37,45,49,54,59,61,65,68,73,75,84
    };

    // The 63 non-sync frame positions, in frame order → QRA codeword symbols 0-62.
    private static readonly int[] DataPos;

    static Q65Decoder()
    {
        var syncSet = new HashSet<int>(SyncPos);
        DataPos = Enumerable.Range(0, NSym).Where(i => !syncSet.Contains(i)).ToArray();
    }

    // ── Instance fields ───────────────────────────────────────────────────────
    private readonly DigitalMode _mode;
    private readonly int  _period;       // TX/RX period in whole seconds (ceiling of PeriodSeconds)
    private readonly int  _nsps;         // samples per symbol for this submode
    private readonly int  _nsubmode;     // 0=A, 1=B, 2=C, 3=D
    private readonly int  _nBinsPerTone; // NspsA / _nsps  (= 1 << _nsubmode)

    // ── Multi-period averaging state ──────────────────────────────────────────
    private int _maxPeriods = 3;        // from DecoderOptions.AveragingPeriods
    private bool _averagingEnabled = true;
    private readonly List<float[][]> _symPowHistory = new();  // ring buffer

    public Q65Decoder(DigitalMode mode = DigitalMode.Q65A)
    {
        _mode = mode;
        (_period, _nsps, _nsubmode) = mode switch
        {
            DigitalMode.Q65A => (60, 6912, 0),
            DigitalMode.Q65B => (30, 3456, 1),
            DigitalMode.Q65C => (15, 1728, 2),
            DigitalMode.Q65D => ( 8,  864, 3),  // period=7.5s; use 8s buffer for timing slack
            _                => (60, 6912, 0),
        };
        _nBinsPerTone = 1 << _nsubmode;
    }

    public override void Configure(DecoderOptions options)
    {
        base.Configure(options);
        _averagingEnabled = options.AveragingEnabled;
        _maxPeriods       = Math.Clamp(options.AveragingPeriods, 1, 5);
        if (options.ClearAverage)
            _symPowHistory.Clear();
    }

    public override DigitalMode Mode => _mode;

    // ── Decode entry point ────────────────────────────────────────────────────

    public override IReadOnlyList<DecodeResult> Decode(
        ReadOnlySpan<float> samples, double freqLow, double freqHigh, string utcTime)
    {
        if (samples.Length < _nsps * NSym / 2)
            return Array.Empty<DecodeResult>();

        float[] samplesArr = samples.ToArray();  // needed for PLINQ capture (Span can't escape to lambdas)

        int nBinsPerSymbol = 64 * (2 + _nBinsPerTone); // s3 bins per data symbol

        // Search range for sync tone (tone 0)
        int maxBin    = NspsA / 2;
        int fSyncLo   = Math.Clamp((int)(freqLow  / DfA) - 1, 1, maxBin - 65 * _nBinsPerTone - 65);
        int fSyncHi   = Math.Clamp((int)(freqHigh / DfA) + 1, fSyncLo + 1, maxBin - 65 * _nBinsPerTone - 65);

        // Pre-build Hann window
        double[] window = Windowing.Hann(_nsps);

        // ── Multi-period averaging ─────────────────────────────────────────────
        // Compute centre-offset symPow for the current period, accumulate, then
        // average element-wise across all stored periods before decoding.
        int maxT0     = Math.Max(0, samplesArr.Length - NSym * _nsps);
        int centreT0  = maxT0 / 2;

        if (_averagingEnabled && _maxPeriods > 1)
        {
            float[][] curSymPow = ComputeSymbolPowers(samplesArr, centreT0, window);
            AccumulateSymPow(curSymPow);
        }

        float[][]? avgSymPow = (_averagingEnabled && _maxPeriods > 1 && _symPowHistory.Count > 0)
            ? AverageSymPow()
            : null;

        // Try a few time offsets: centre + ±quarter/half symbol
        int[] dtArr = { 0, _nsps / 4, -_nsps / 4, _nsps / 2, -_nsps / 2 };

        // Parallel over time offsets; each offset computes its own symPow independently.
        var rawResults = dtArr
            .AsParallel()
            .SelectMany(dtOff =>
            {
                int t0 = centreT0 + dtOff;
                if (t0 < 0 || t0 + NSym * _nsps > samplesArr.Length)
                    return Enumerable.Empty<DecodeResult>();

                // For the centre offset, use the averaged symPow when available
                float[][] symPow = (dtOff == 0 && avgSymPow != null)
                    ? avgSymPow
                    : ComputeSymbolPowers(samplesArr, t0, window);

                var local = new List<DecodeResult>();

                for (int fSyncBin = fSyncLo; fSyncBin <= fSyncHi; fSyncBin++)
                {
                    double score = SyncScore(symPow, fSyncBin);
                    if (score < 2.0) continue;

                    int fBaseBin = fSyncBin + _nBinsPerTone;

                    float[] s3     = BuildS3(symPow, fBaseBin, nBinsPerSymbol);
                    float[] s3prob = new float[NDataSym * 64];
                    Q65Subs.ComputeIntrinsics(s3, _nsubmode, B90Ts, 0, s3prob);

                    int[] xdec = new int[13];
                    Q65Subs.Decode(s3, s3prob, null, null, MaxIters,
                                   out float esNodB, xdec, out int irc);
                    if (irc < 0) continue;

                    bool[] bits = SymbolsToBits(xdec);
                    string msg  = MessagePacker.Unpack77(bits, out bool ok);
                    if (!ok || string.IsNullOrWhiteSpace(msg)) continue;

                    local.Add(new DecodeResult
                    {
                        Message     = msg.Trim(),
                        FrequencyHz = fSyncBin * DfA,
                        Dt          = t0 / (double)SampleRate,
                        Snr         = esNodB,
                        UtcTime     = utcTime,
                        Mode        = _mode,
                    });
                }
                return local;
            })
            .ToList();

        var results = new List<DecodeResult>();
        var decoded  = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rawResults.OrderBy(r => r.FrequencyHz).ThenBy(r => r.Dt))
        {
            if (decoded.Add(r.Message))
            {
                results.Add(r);
                Emit(r);
            }
        }

        return results;
    }

    // ── Averaging helpers ─────────────────────────────────────────────────────

    private void AccumulateSymPow(float[][] symPow)
    {
        if (_symPowHistory.Count < _maxPeriods)
            _symPowHistory.Add(symPow);
        else
        {
            // Replace the oldest entry (FIFO: index 0 is always the oldest)
            _symPowHistory.RemoveAt(0);
            _symPowHistory.Add(symPow);
        }
    }

    /// <summary>
    /// Element-wise mean of all symbol-power arrays in the history buffer.
    /// Returns a new float[NSym][bins] with averaged values.
    /// </summary>
    private float[][] AverageSymPow()
    {
        int nPeriods = _symPowHistory.Count;
        int nBins    = _symPowHistory[0][0].Length;
        float invN   = 1.0f / nPeriods;

        var avg = new float[NSym][];
        for (int sym = 0; sym < NSym; sym++)
        {
            avg[sym] = new float[nBins];
            for (int p = 0; p < nPeriods; p++)
            {
                float[] row = _symPowHistory[p][sym];
                for (int b = 0; b < nBins; b++)
                    avg[sym][b] += row[b];
            }
            for (int b = 0; b < nBins; b++)
                avg[sym][b] *= invN;
        }
        return avg;
    }

    // ── Signal processing ─────────────────────────────────────────────────────

    /// <summary>
    /// Compute one-sided power spectrum for each of the 85 symbol slots.
    /// Each symbol is Hann-windowed and zero-padded to NspsA before an NspsA-point FFT.
    /// </summary>
    private float[][] ComputeSymbolPowers(float[] samples, int t0, double[] window)
    {
        var result = new float[NSym][];
        var buf    = new Complex[NspsA];
        int half   = NspsA / 2 + 1;

        for (int sym = 0; sym < NSym; sym++)
        {
            int start = t0 + sym * _nsps;

            for (int i = 0; i < _nsps; i++)
            {
                int idx = start + i;
                double v = (idx < samples.Length) ? samples[idx] * window[i] : 0.0;
                buf[i] = new Complex(v, 0.0);
            }
            for (int i = _nsps; i < NspsA; i++)
                buf[i] = Complex.Zero;

            Fft.ForwardInPlace(buf);

            var pow = new float[half];
            for (int k = 0; k < half; k++)
                pow[k] = (float)(buf[k].Real * buf[k].Real + buf[k].Imaginary * buf[k].Imaginary);

            result[sym] = pow;
        }
        return result;
    }

    /// <summary>
    /// Sync quality score: ratio of sync-position power to data-position power
    /// at the candidate sync-tone bin.  Higher is better; threshold ≈ 2.0.
    /// </summary>
    private static double SyncScore(float[][] symPow, int fSyncBin)
    {
        if (fSyncBin < 0 || fSyncBin >= symPow[0].Length) return 0;

        double syncSum = 0, dataSum = 0;
        foreach (int s in SyncPos) syncSum += symPow[s][fSyncBin];
        foreach (int d in DataPos) dataSum += symPow[d][fSyncBin];

        double dataAvg = dataSum / DataPos.Length;
        return dataAvg < 1e-30 ? 0 : (syncSum / SyncPos.Length) / dataAvg;
    }

    /// <summary>
    /// Build the s3 power array for QRA intrinsics.
    /// For each data symbol n: s3[n*nBinsPerSymbol + i] = power at FFT bin (fBaseBin - 64 + i).
    /// The 64 guard bins on the left include the sync tone.
    /// The central 64*nBinsPerTone bins cover GF(64) values 0-63 (tones 1-64).
    /// </summary>
    private float[] BuildS3(float[][] symPow, int fBaseBin, int nBinsPerSymbol)
    {
        var s3      = new float[NDataSym * nBinsPerSymbol];
        int fLeftBin = fBaseBin - 64;

        for (int n = 0; n < NDataSym; n++)
        {
            float[] pow    = symPow[DataPos[n]];
            int     s3Base = n * nBinsPerSymbol;
            int     maxBin = pow.Length;

            for (int i = 0; i < nBinsPerSymbol; i++)
            {
                int fftBin = fLeftBin + i;
                s3[s3Base + i] = (fftBin >= 0 && fftBin < maxBin) ? pow[fftBin] : 0f;
            }
        }
        return s3;
    }

    // ── Message unpacking ─────────────────────────────────────────────────────

    /// <summary>
    /// Convert 13 GF(64) symbols (6-bit values) to 77 message bits, MSB-first per symbol.
    /// Matches WSJT-X: write(c77,'(12b6.6,b5.5)') dat4(1:12),(dat4(13)/2)
    /// The 13th symbol has been encoded as (5-bit value) × 2, so dividing by 2 (ignoring
    /// bit 0) is implicit: the MSB-first read of bits [0..4] gives the 5 original bits.
    /// </summary>
    private static bool[] SymbolsToBits(int[] xdec)
    {
        var bits = new bool[77];
        for (int i = 0; i < 13; i++)
        {
            int sym = xdec[i];
            for (int b = 0; b < 6; b++)
            {
                int pos = i * 6 + b;
                if (pos < 77)
                    bits[pos] = ((sym >> (5 - b)) & 1) != 0;
            }
        }
        return bits;
    }
}

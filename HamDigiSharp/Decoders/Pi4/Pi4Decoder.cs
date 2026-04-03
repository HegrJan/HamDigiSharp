using HamDigiSharp.Models;

namespace HamDigiSharp.Decoders.Pi4;

/// <summary>
/// PI4 decoder — π/4-QPSK with rate-1/2 K=32 convolutional code, 30-second period.
/// C# port of MSHV's PI4 decoder (LZ2HV, based on K1JT JT4), GPL.
///
/// Pipeline:
///   1. Spectrogram → spectral flatten (pspi4 / flat1b)
///   2. Cross-correlate with PRN sequence to find dtx / dfx (xcorpi4)
///   3. Coherent QPSK detection at each symbol (decodepi4)
///   4. Normalize, scale to int8, deinterleave (extractpi4)
///   5. Fano sequential decoder R=1/2, K=32 (fano232)
///   6. Extract 8-char message from 42 data bits in base-38
///
/// Constants from decoderpi4.cpp:
///   N_SYMMAX=2000, G_NSYM=146, SampleRate=11025
///   df = 11025/2048 ≈ 5.384 Hz/bin
///   dt = 2/11025 (2 samples per phase integration step)
/// </summary>
public sealed class Pi4Decoder : BaseDecoder
{
    // ── Protocol constants ────────────────────────────────────────────────────
    private const int   SampleRate  = 11025;
    private const int   NSymMax     = 2000;   // samples per symbol × 2 (dt=2/SR)
    private const int   Nsym        = 146;    // symbols per PI4 frame
    private const int   NhMax       = NSymMax / 2; // 1000 — FFT half size for sync
    private const double CenterFreq = 682.8125; // Hz default

    private const uint Npoly1 = 0xf2d05351;
    private const uint Npoly2 = 0xe4613c47;
    private const int  OffMet = 128; // mettab index offset
    private const int  NBits  = 42;  // data bits (before tail)
    private const int  MaxCycles = 20000;

    // PRN sync sequence (npr2_pi4 from decoderpi4.cpp)
    private static readonly int[] Npr2 =
    {
        0,0,1,0,0,1,1,1,1,0,1,0,1,0,1,0,0,1,0,0,0,1,0,0,0,1,1,0,0,1,
        1,1,1,0,0,1,1,1,1,1,0,0,1,1,0,1,1,1,1,0,1,0,1,1,0,1,1,0,1,0,
        0,0,0,0,1,1,1,1,1,0,1,0,1,0,0,0,0,0,1,1,1,1,1,0,1,0,0,1,0,0,
        1,0,1,0,0,0,0,1,0,0,1,1,0,0,0,0,0,1,1,0,0,0,0,1,1,0,0,1,1,1,
        0,1,1,1,0,1,1,0,1,0,1,0,1,0,0,0,0,1,1,1,0,0,0,0,1,1
    };

    // parity table (partab_pi4 from decoderpi4.cpp)
    private static readonly int[] Partab =
    {
        0,1,1,0,1,0,0,1,1,0,0,1,0,1,1,0,1,0,0,1,0,1,1,0,0,1,1,0,1,0,0,1,
        1,0,0,1,0,1,1,0,0,1,1,0,1,0,0,1,0,1,1,0,1,0,0,1,1,0,0,1,0,1,1,0,
        1,0,0,1,0,1,1,0,0,1,1,0,1,0,0,1,0,1,1,0,1,0,0,1,1,0,0,1,0,1,1,0,
        0,1,1,0,1,0,0,1,1,0,0,1,0,1,1,0,1,0,0,1,0,1,1,0,0,1,1,0,1,0,0,1,
        1,0,0,1,0,1,1,0,0,1,1,0,1,0,0,1,0,1,1,0,1,0,0,1,1,0,0,1,0,1,1,0,
        0,1,1,0,1,0,0,1,1,0,0,1,0,1,1,0,1,0,0,1,0,1,1,0,0,1,1,0,1,0,0,1,
        0,1,1,0,1,0,0,1,1,0,0,1,0,1,1,0,1,0,0,1,0,1,1,0,0,1,1,0,1,0,0,1,
        1,0,0,1,0,1,1,0,0,1,1,0,1,0,0,1,0,1,1,0,1,0,0,1,1,0,0,1,0,1,1,0
    };

    // Valid message characters (PI4_valid_chars from decoderpi4.cpp)
    private static readonly char[] ValidChars =
    {
        '0','1','2','3','4','5','6','7',
        '8','9','A','B','C','D','E','F',
        'G','H','I','J','K','L','M','N',
        'O','P','Q','R','S','T','U','V',
        'W','X','Y','Z',' ','/'
    };

    // Metric table and delta (built by GetMetPi4)
    private readonly int[,] _mettab = new int[2, 512]; // indexed [0..1][0..511], offset=256 from OffMet
    private int _ndelta;
    // Interleave permutation (built by BuildInterleave)
    private readonly int[] _j0 = new int[Nsym];

    public Pi4Decoder()
    {
        GetMetPi4();
        BuildInterleave();
    }

    public override DigitalMode Mode => DigitalMode.PI4;

    // ── Decode entry point ────────────────────────────────────────────────────

    public override IReadOnlyList<DecodeResult> Decode(
        ReadOnlySpan<float> samples, double freqLow, double freqHigh, string utcTime)
    {
        if (samples.Length < SampleRate * 14) return Array.Empty<DecodeResult>();

        double[] dat = new double[samples.Length];
        for (int i = 0; i < samples.Length; i++) dat[i] = samples[i];

        var results = new List<DecodeResult>();
        var seen = new HashSet<string>();

        // Sweep over frequency range (PI4 is at a fixed offset per station)
        double fCenter = (freqLow + freqHigh) / 2.0;
        double fStep = Math.Max(5.0, (freqHigh - freqLow) / 10);

        for (double nfreq = freqLow; nfreq <= freqHigh; nfreq += fStep)
        {
            // Try different time offsets
            for (int istart = 0; istart < Math.Min(SampleRate, dat.Length / 14); istart += 500)
            {
                string? decoded = null;
                int nfano = 0;
                DecodePI4(dat, istart, dat.Length, nfreq, mode4: 1, ref decoded, ref nfano);

                if (nfano > 0 && decoded != null && decoded.Length >= 4 && seen.Add(decoded))
                {
                    var r = new DecodeResult
                    {
                        UtcTime = utcTime,
                        Snr = EstimateSnr(0, double.NaN),
                        Dt = istart / (double)SampleRate,
                        FrequencyHz = nfreq,
                        Message = decoded,
                        Mode = DigitalMode.PI4,
                    };
                    results.Add(r);
                    Emit(r);
                }
            }
        }
        return results;
    }

    // ── QPSK demodulator (decodepi4 from decoderpi4.cpp) ────────────────────

    private void DecodePI4(double[] dat, int istart, int npts, double nfreq, int mode4,
        ref string? decoded, ref int nfano)
    {
        double dt = 2.0 / SampleRate;
        double df = SampleRate / 2048.0;
        int nsym = Nsym - 1;
        double amp = 15.0;
        int nchips = mode4;
        int nspchip = (NSymMax / 2) / nchips; // samples per chip
        double fac2 = 0.0001 * Math.Sqrt(mode4);

        double[] sym = new double[Nsym];
        // Track accumulated phase as a scalar for cross-symbol continuity.
        double phi0 = 0, phi1 = 0;
        int k = istart;

        for (int j = 0; j <= nsym; j++)
        {
            double f0 = nfreq + Npr2[j] * mode4 * df;
            double f1 = nfreq + (2 + Npr2[j]) * mode4 * df;
            double dphi0 = 2 * Math.PI * dt * f0;
            double dphi1 = 2 * Math.PI * dt * f1;

            // Pre-compute per-sample phase rotation as (cos, sin) pairs.
            // Replaces 4 trig calls/sample with 2 complex multiplications — ~2–3× faster.
            double rotCos0 = Math.Cos(dphi0), rotSin0 = Math.Sin(dphi0);
            double rotCos1 = Math.Cos(dphi1), rotSin1 = Math.Sin(dphi1);

            // Initialise running phasor at phi + dphi (i.e. first sample of this symbol)
            phi0 += dphi0;
            phi1 += dphi1;
            double pCos0 = Math.Cos(phi0), pSin0 = Math.Sin(phi0);
            double pCos1 = Math.Cos(phi1), pSin1 = Math.Sin(phi1);

            double sq0 = 0, sq1 = 0;

            for (int nc = 0; nc < nchips; nc++)
            {
                double cRe0 = 0, cIm0 = 0, cRe1 = 0, cIm1 = 0;
                for (int i = 0; i < nspchip; i++)
                {
                    if (k >= 0 && k < npts)
                    {
                        double d = dat[k];
                        cRe0 += d * pCos0; cIm0 -= d * pSin0;
                        cRe1 += d * pCos1; cIm1 -= d * pSin1;
                    }
                    k++;

                    // Advance phasor via complex multiplication (no trig calls)
                    double nCos0 = pCos0 * rotCos0 - pSin0 * rotSin0;
                    pSin0 = pCos0 * rotSin0 + pSin0 * rotCos0;
                    pCos0 = nCos0;

                    double nCos1 = pCos1 * rotCos1 - pSin1 * rotSin1;
                    pSin1 = pCos1 * rotSin1 + pSin1 * rotCos1;
                    pCos1 = nCos1;
                }
                sq0 += cRe0 * cRe0 + cIm0 * cIm0;
                sq1 += cRe1 * cRe1 + cIm1 * cIm1;
            }

            // Advance scalar accumulators to match the phasor (last sample already advanced)
            phi0 += dphi0 * (nspchip * nchips - 1);
            phi1 += dphi1 * (nspchip * nchips - 1);

            sym[j] = amp * (Math.Sqrt(fac2 * sq1) - Math.Sqrt(fac2 * sq0));
        }

        int ncount = -1;
        decoded = ExtractPi4(sym, ref ncount);

        nfano = ncount >= 0 ? 1 : (ncount == -2 ? -1 : 0);
    }

    // ── Extract, interleave, Fano decode, message (extractpi4) ───────────────

    private string? ExtractPi4(double[] sym0, ref int ncount)
    {
        // Normalize
        double ave = 0;
        for (int i = 0; i < Nsym; i++) ave += sym0[i];
        ave /= Nsym;

        double[] sym = new double[Nsym];
        double sq = 0;
        for (int i = 0; i < Nsym; i++) { sym[i] = sym0[i] - ave; sq += sym[i] * sym[i]; }
        double rms = Math.Sqrt(sq / Nsym);
        if (rms == 0) rms = 1.0;
        for (int i = 0; i < Nsym; i++) sym[i] /= rms;

        // Scale to int8 range
        double amp = 30.0;
        sbyte[] symbol = new sbyte[Nsym];
        for (int j = 0; j < Nsym; j++)
        {
            int n = (int)(amp * sym[j]);
            symbol[j] = (sbyte)Math.Clamp(n, -128, 127);
        }

        // Deinterleave (ndir=-1)
        sbyte[] tmp = new sbyte[Nsym];
        for (int i = 0; i < Nsym; i++)
            tmp[i] = symbol[_j0[i]];
        symbol = tmp;

        // Fano decode (nbits=NBits+31=73, maxcycles=MaxCycles)
        byte[] data1 = new byte[14];
        int ncycles = 0;
        Fano232(symbol, 0, NBits + 31, MaxCycles, data1, ref ncycles, ref ncount);

        if (ncount < 0) return null;

        // Extract 8-char message: 42 bits → base-38
        long dataVal = ((long)data1[0] << 34)
                     | ((long)data1[1] << 26)
                     | ((long)data1[2] << 18)
                     | ((long)data1[3] << 10)
                     | ((long)data1[4] << 2)
                     | ((long)data1[5] >> 6);

        char[] msg = new char[8];
        for (int i = 7; i >= 0; i--)
        {
            msg[i] = ValidChars[dataVal % 38];
            dataVal /= 38;
        }
        string decoded = new string(msg);

        // Reject all-zero (invalid) decode
        if (decoded == "00000000" || decoded == "AWDA5SEY")
        {
            ncount = -2;
            return null;
        }
        return decoded;
    }

    // ── Fano sequential decoder (fano232 from decoderpi4.cpp) ────────────────
    // Decodes R=1/2, K=32 convolutional code using Fano algorithm.

    private void Fano232(sbyte[] symbol, int beg, int nbits, int maxcycles,
        byte[] dat, ref int ncycles, ref int ierr)
    {
        const int MaxBits = 104;
        int[,] metrics = new int[4, MaxBits];
        int[] nstate = new int[MaxBits];
        int[,] tm = new int[2, MaxBits];
        int[] ii = new int[MaxBits];
        int[] gamma = new int[MaxBits];

        int ntail = nbits - 31;
        int np = 0;

        // Precompute branch metrics for all bit positions
        for (int k = 0; k <= nbits - 1; k++)
        {
            int j = 2 * k;
            int i4a = (symbol[j + beg] + OffMet) & 0xFF;
            int i4b = (symbol[j + 1 + beg] + OffMet) & 0xFF;
            metrics[0, k] = _mettab[0, i4a] + _mettab[0, i4b];
            metrics[1, k] = _mettab[0, i4a] + _mettab[1, i4b];
            metrics[2, k] = _mettab[1, i4a] + _mettab[0, i4b];
            metrics[3, k] = _mettab[1, i4a] + _mettab[1, i4b];
        }

        nstate[0] = 0;
        int lsym = Encode(nstate[np]);
        int m0 = metrics[lsym, np];
        int m1 = metrics[3 ^ lsym, np];
        if (m0 > m1) { tm[0, np] = m0; tm[1, np] = m1; }
        else { tm[0, np] = m1; tm[1, np] = m0; nstate[np]++; }
        ii[np] = 0;
        gamma[np] = 0;
        int nt = 0;

        int i;
        for (i = 1; i < nbits * maxcycles; i++)
        {
            int ngamma = gamma[np] + tm[ii[np], np];
            if (ngamma >= nt)
            {
                if (gamma[np] < nt + _ndelta)
                    nt = nt + _ndelta * ((ngamma - nt) / _ndelta);
                gamma[np + 1] = ngamma;
                nstate[np + 1] = nstate[np] << 1;
                np++;
                if (np == nbits) goto Done;

                lsym = Encode(nstate[np]);
                if (np >= ntail)
                {
                    tm[0, np] = metrics[lsym, np];
                }
                else
                {
                    m0 = metrics[lsym, np];
                    m1 = metrics[3 ^ lsym, np];
                    if (m0 > m1) { tm[0, np] = m0; tm[1, np] = m1; }
                    else { tm[0, np] = m1; tm[1, np] = m0; nstate[np]++; }
                }
                ii[np] = 0;
            }
            else
            {
                while (true)
                {
                    bool noback = np == 0 || (np > 0 && gamma[np - 1] < nt);
                    if (noback)
                    {
                        nt -= _ndelta;
                        if (ii[np] != 0) { ii[np] = 0; nstate[np] ^= 1; }
                        break;
                    }
                    np--;
                    if (np < ntail && ii[np] != 1) { ii[np]++; nstate[np] ^= 1; break; }
                }
            }
        }
        i = nbits * maxcycles;

    Done:
        int nbytes = (nbits + 7) / 8;
        int ptr = 7;
        for (int j = 0; j <= nbytes - 1; j++)
        {
            dat[j] = (byte)nstate[ptr];
            ptr += 8;
        }
        ncycles = i + 1;
        ierr = 0;
        if (i >= maxcycles * nbits) ierr = -1;
    }

    // ENCODE macro: compute encoder output symbol for current state
    private static int Encode(int encstate)
    {
        uint tmp = (uint)encstate & Npoly1;
        tmp ^= tmp >> 16;
        int sym = Partab[(tmp ^ (tmp >> 8)) & 0xFF];
        tmp = (uint)encstate & Npoly2;
        tmp ^= tmp >> 16;
        sym = sym + sym + Partab[(tmp ^ (tmp >> 8)) & 0xFF];
        return sym;
    }

    // ── Build metric table (getmetpi4 from decoderpi4.cpp) ───────────────────

    private void GetMetPi4()
    {
        double[] xx0 = {
            1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,
            1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,
            1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,1.000,
            0.988,1.000,0.991,0.993,1.000,0.995,1.000,0.991,1.000,0.991,0.992,0.991,0.990,0.990,0.992,0.996,
            0.990,0.994,0.993,0.991,0.992,0.989,0.991,0.987,0.985,0.989,0.984,0.983,0.979,0.977,0.971,0.975,
            0.974,0.970,0.970,0.970,0.967,0.962,0.960,0.957,0.956,0.953,0.942,0.946,0.937,0.933,0.929,0.920,
            0.917,0.911,0.903,0.895,0.884,0.877,0.869,0.858,0.846,0.834,0.821,0.806,0.790,0.775,0.755,0.737,
            0.713,0.691,0.667,0.640,0.612,0.581,0.548,0.510,0.472,0.425,0.378,0.328,0.274,0.212,0.146,0.075,
            0.000,-0.079,-0.163,-0.249,-0.338,-0.425,-0.514,-0.606,-0.706,-0.796,-0.895,-0.987,-1.084,-1.181,-1.280,-1.376,
            -1.473,-1.587,-1.678,-1.790,-1.882,-1.992,-2.096,-2.201,-2.301,-2.411,-2.531,-2.608,-2.690,-2.829,-2.939,-3.058,
            -3.164,-3.212,-3.377,-3.463,-3.550,-3.768,-3.677,-3.975,-4.062,-4.098,-4.186,-4.261,-4.472,-4.621,-4.623,-4.608,
            -4.822,-4.870,-4.652,-4.954,-5.108,-5.377,-5.544,-5.995,-5.632,-5.826,-6.304,-6.002,-6.559,-6.369,-6.658,-7.016,
            -6.184,-7.332,-6.534,-6.152,-6.113,-6.288,-6.426,-6.313,-9.966,-6.371,-9.966,-7.055,-9.966,-6.629,-6.313,-9.966,
            -5.858,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,
            -9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,
            -9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966,-9.966
        };
        double bias = 0.45, scale = 50.0;
        _ndelta = (int)(3.4 * scale); // 170

        for (int i = 0; i < 256; i++)
        {
            double xx = i >= 160
                ? xx0[160] - (i - 160.0) * 6.822 / 65.3
                : xx0[i];
            int val = (int)(scale * (xx - bias));
            // mettab[0][(i-128)+OffMet]  → index (i-128)+128 = i  (range 0..255)
            _mettab[0, i] = val;
            // mettab[1][(128-i)+OffMet]  → index (128-i)+128 = 256-i (range 1..256)
            if (i >= 1) _mettab[1, 256 - i] = val;
        }
        // Boundary: mettab[1][-128+OffMet=0] = mettab[1][-127+OffMet=1]
        _mettab[1, 0] = _mettab[1, 1];
    }

    // ── Build interleave permutation (interleavepi4 from decoderpi4.cpp) ─────

    private void BuildInterleave()
    {
        int k = -1;
        for (int i = 0; i < 256; i++)
        {
            int m = i;
            // 8-bit reversal
            int n = m & 1;
            n = 2 * n + ((m / 2) & 1);
            n = 2 * n + ((m / 4) & 1);
            n = 2 * n + ((m / 8) & 1);
            n = 2 * n + ((m / 16) & 1);
            n = 2 * n + ((m / 32) & 1);
            n = 2 * n + ((m / 64) & 1);
            n = 2 * n + ((m / 128) & 1);
            if (n <= Nsym - 1)
            {
                k++;
                _j0[k] = n;
            }
        }
    }
}

using System.Numerics;
using System.Text;
using HamDigiSharp.Dsp;
using HamDigiSharp.Models;

namespace HamDigiSharp.Decoders.Iscat;

/// <summary>
/// ISCAT-A and ISCAT-B decoder — 30-second period, no FEC.
/// Port of MSHV's <c>decoderiscat.cpp</c> (LZ2HV / K1JT, GPL).
///
/// Algorithm:
///   1. FFT-based analytic downsampling: 11025 Hz → 3100.78 Hz (factor 9/32)
///   2. Multi-scale sync search (STFT + Costas {0,1,3,2} folding)
///   3. Character decode: spectral-peak argmax per symbol position
/// </summary>
public sealed class IscatDecoder : BaseDecoder
{
    // ── Protocol constants ────────────────────────────────────────────────────
    private const int    SampleRate = 11025;
    private const int    NMax       = SampleRate * 30;   // 330750 raw samples
    private const double Fsample    = 3100.78125;        // downsampled rate
    private const int    NSync      = 4;
    private const int    NLen       = 2;
    private const int    NDat       = 18;
    private const int    NBlk       = NSync + NLen + NDat; // 24 symbols/block

    // 42-character alphabet: 0-9, A-Z, space, /, ., ?, @, -
    private static readonly char[] CharTable =
    {
        '0','1','2','3','4','5','6','7','8','9',
        'A','B','C','D','E','F','G','H','I','J','K','L','M',
        'N','O','P','Q','R','S','T','U','V','W','X','Y','Z',
        ' ','/','.','?','@','-'
    };

    // Costas sync array
    private static readonly int[] Icos = { 0, 1, 3, 2 };

    private readonly DigitalMode _mode;
    private readonly int         _mode4; // 1 = ISCAT-A, 2 = ISCAT-B

    public IscatDecoder(DigitalMode mode)
    {
        _mode  = mode;
        _mode4 = mode == DigitalMode.IscatA ? 1 : 2;
    }

    public override DigitalMode Mode => _mode;

    // ── Public entry point ────────────────────────────────────────────────────

    public override IReadOnlyList<DecodeResult> Decode(
        ReadOnlySpan<float> samples, double freqLow, double freqHigh, string utcTime)
    {
        int npts0 = Math.Min(samples.Length, NMax);
        if (npts0 < 1) return Array.Empty<DecodeResult>();

        Complex[] cdat0 = Ana932(samples, npts0);
        var results = new List<DecodeResult>();
        DecodeIscat(cdat0, freqLow, freqHigh, utcTime, results);
        return results;
    }

    // ── Analytic downsampling (9/32): 11025 → 3100.78 Hz ─────────────────────
    // Port of MSHV ana932(): forward real FFT → keep first nfft2 bins → IFFT.
    private static Complex[] Ana932(ReadOnlySpan<float> dat, int npts0)
    {
        int n     = (int)(Math.Log(npts0) / Math.Log(2.0));
        int nfft1 = 1 << (n + 1);
        int nfft2 = (int)(9.0 * nfft1 / 32.0);

        var c = new Complex[nfft1];
        int copyLen = Math.Min(npts0, nfft1);
        for (int i = 0; i < copyLen; i++) c[i] = new Complex(dat[i], 0.0);
        Fft.ForwardInPlace(c);   // no normalization (AsymmetricScaling)

        // Keep only low-frequency (analytic) half, downsample in frequency domain
        var c2 = c[..nfft2].ToArray();
        Fft.InverseInPlace(c2); // 1/nfft2 normalization — scale irrelevant (pctile normalised)

        int npts = (int)(npts0 * 9.0 / 32.0);
        return npts < c2.Length ? c2[..npts] : c2;
    }

    // ── Main decoder: multi-scale search + character readout ─────────────────
    private void DecodeIscat(
        Complex[] cdat0, double freqLow, double freqHigh,
        string utcTime, List<DecodeResult> results)
    {
        int npts0 = cdat0.Length;
        int nsps  = 144 / _mode4;         // A=144, B=72
        int nfft  = 2 * nsps;             // A=288, B=144
        int kstep = nsps / 4;             // A=36,  B=18
        double df = Fsample / nfft;

        // Frequency search setup from caller bounds
        double fCenter = (freqLow + freqHigh) / 2.0;
        int dfTol = Math.Clamp((int)((freqHigh - freqLow) / 2.0), 100, 2000);
        int i0    = Math.Clamp((int)(fCenter / df), 10, nfft - 20);

        int s0Cols    = nfft + 1;  // bin indices 0..nfft (last bin stays 0)
        int maxNFrames = npts0 / (24 * nsps);
        if (maxNFrames <= 0) return;
        int maxJsym = 4 * (maxNFrames * 24 * nsps / nsps);

        // Pre-allocate all reusable buffers (avoid allocations in hot loops)
        var s0Flat = new double[(maxJsym + 4) * s0Cols];
        var fs0    = new double[96 * s0Cols];
        var savg   = new double[s0Cols];
        var sref   = new double[s0Cols];
        var cSym   = new Complex[nfft];
        var fs1    = new double[60 * 42];
        var nsum   = new int[60];
        var sb     = new StringBuilder(42);

        // Best-result accumulators
        double bigWorst = -1e30, bigAvg = 0.0, bigXsync = 0.0, bigSig = -1e30;
        int    ndf0Big = 0, ipkBig = i0;
        string msgBig  = "";
        double bigT2   = 0.0;
        bool   last    = false;

        for (int inf = 1; inf <= 6 && !last; inf++)
        {
            int nframes = 1 << inf;
            if (nframes * 24 * nsps > npts0)
            {
                nframes = npts0 / (24 * nsps);
                last = true;
            }
            if (nframes <= 0) break;
            int npts = nframes * 24 * nsps;

            bool earlyStop = false;
            for (int ia = 0; ia < npts0 - npts && !earlyStop; ia += npts)
            {
                SyncIscat(cdat0, ia, npts, nsps, nfft, kstep, df, i0, dfTol,
                    s0Flat, s0Cols, fs0, savg, sref, cSym,
                    out int jsym, out double xsync, out double sig,
                    out int ndf0, out int msgLen, out int ipk, out int jpk);

                if (msgLen == 0 || xsync < 0.0) continue;

                double t3 = (ia + 0.5 * npts) / Fsample + 0.9;

                // Fold data symbols into fs1[msgLen][42]
                Array.Clear(fs1);
                Array.Clear(nsum, 0, 60);
                int k = 0, nChar = 0;
                int jMax = Math.Min(jsym + 1, s0Flat.Length / s0Cols);
                for (int j = jpk; j <= jsym && j < jMax; j += 4)
                {
                    if (k % NBlk > NSync + NLen - 1) // km >= 6: data symbols
                    {
                        int m = nChar % msgLen;
                        for (int ci = 0; ci < 42; ci++)
                        {
                            int iii = ipk + 2 * ci;
                            if ((uint)iii < (uint)s0Cols)
                                fs1[m * 42 + ci] += s0Flat[j * s0Cols + iii];
                        }
                        nChar++;
                        nsum[m]++;
                    }
                    k++;
                }

                // Normalise per character slot
                for (int m = 0; m < msgLen; m++)
                {
                    double div = nsum[m] > 0 ? nsum[m] : 1.0;
                    for (int ci = 0; ci < 42; ci++)
                        fs1[m * 42 + ci] /= div;
                }

                // Decode characters: argmax + ratio-to-second-best
                sb.Clear();
                double worst = 9999.0, sum = 0.0;
                int    mpk  = -1;
                for (int m = 0; m < msgLen; m++)
                {
                    double smax = 0.0, smax2 = 0.0;
                    int    ipk3 = 0;
                    for (int ci = 0; ci < 42; ci++)
                        if (fs1[m * 42 + ci] > smax) { smax = fs1[m * 42 + ci]; ipk3 = ci; }
                    for (int ci = 0; ci < 42; ci++)
                        if (fs1[m * 42 + ci] > smax2 && ci != ipk3) smax2 = fs1[m * 42 + ci];
                    double rr = smax2 > 0.0 ? smax / smax2 : 0.0;
                    sum += rr;
                    if (rr < worst) worst = rr;
                    if (ipk3 == 40) mpk = m; // '@' sync character
                    sb.Append(CharTable[ipk3]);
                }

                double avg   = sum / Math.Max(1, msgLen);
                string msg1  = sb.ToString();
                // Rotate at '@': strip the sync marker and align message start
                string msg   = mpk >= 0 && mpk < msgLen
                    ? msg1[(mpk + 1)..] + msg1[..mpk]
                    : msg1;

                if (worst > bigWorst)
                {
                    bigWorst = worst; bigAvg = avg; bigXsync = xsync; bigSig = sig;
                    ndf0Big  = ndf0;  ipkBig  = ipk; msgBig  = msg;  bigT2  = t3;
                }

                if (avg > 2.5 && xsync >= 1.5 && bigWorst > 2.0)
                    earlyStop = true;
            }
        }

        // Accept final result
        int nAvg = (int)(10.0 * (bigAvg - 1.0));
        if (nAvg <= 0 || bigXsync < 0.0) return;
        if (ndf0Big < -dfTol || ndf0Big > dfTol) return;
        if (string.IsNullOrWhiteSpace(msgBig)) return;

        results.Add(new DecodeResult
        {
            Message     = msgBig.Trim(),
            Snr         = (int)bigSig,
            Dt          = bigT2,
            FrequencyHz = ipkBig * df,
            Mode        = _mode,
            UtcTime     = utcTime,
        });
    }

    // ── Sync search: STFT → noise-normalise → fold → Costas search ───────────
    // Port of MSHV synciscat(). Fills s0Flat and returns sync parameters.
    private void SyncIscat(
        Complex[] cdat0, int offset, int npts, int nsps, int nfft, int kstep,
        double df, int i0, int dfTol,
        double[] s0Flat, int s0Cols,
        double[] fs0, double[] savg, double[] sref, Complex[] cSym,
        out int    jsym,
        out double xsync,
        out double sig,
        out int    ndf0,
        out int    msgLen,
        out int    ipk,
        out int    jpk)
    {
        int nsym = npts / nsps;
        jsym = 4 * nsym;

        Array.Clear(savg, 0, s0Cols);

        // ── STFT: power spectra at quarter-symbol steps ───────────────────────
        int ia = 0;
        for (int j = 0; j < 4 * nsym; j++)
        {
            int symEnd = ia + nsps;
            if (symEnd > npts || offset + symEnd > cdat0.Length) break;

            for (int x = 0; x < nsps; x++)
                cSym[x] = cdat0[offset + ia + x]; // no fac: normalised by pctile
            for (int x = nsps; x < nfft; x++) cSym[x] = Complex.Zero;
            Fft.ForwardInPlace(cSym);

            int rowBase = j * s0Cols;
            for (int i = 0; i < nfft; i++)
            {
                double ps = cSym[i].Real * cSym[i].Real + cSym[i].Imaginary * cSym[i].Imaginary;
                s0Flat[rowBase + i] = ps;
                savg[i] += ps;
            }
            ia += kstep;
        }

        if (jsym > 0)
            for (int i = 0; i < nfft; i++) savg[i] /= jsym;

        // ── Noise floor via 40th-percentile in ±3-bin sliding window ─────────
        const int nh = 3, npct = 40;
        Span<double> pctTmp = stackalloc double[2 * nh + 1];
        for (int i = nh; i < nfft - nh; i++)
        {
            for (int q = 0; q <= 2 * nh; q++) pctTmp[q] = savg[i - nh + q];
            pctTmp[..(2 * nh + 1)].Sort();
            int idx = Math.Clamp((int)((2 * nh + 1) * 0.01 * npct), 0, 2 * nh);
            sref[i] = pctTmp[idx];
        }
        for (int i = 0; i < nh; i++)
        {
            sref[i] = sref[nh + 10];
            sref[nfft - nh + i] = sref[nfft - nh - 1];
        }

        // ── Normalise s0 per frequency bin ───────────────────────────────────
        for (int i = 0; i < nfft; i++)
        {
            double normFac = i >= 10
                ? (sref[i]   > 0 ? 1.0 / sref[i]   : 0.0)
                : (savg[10] > 0 ? 1.0 / savg[10] : 0.0);
            for (int j = 0; j < jsym; j++)
                s0Flat[j * s0Cols + i] *= normFac;
        }

        // ── Fold into fs0 modulo 4*NBlk = 96 ─────────────────────────────────
        Array.Clear(fs0);
        double nfold = jsym / 96.0;
        int    jbInt = (int)(96.0 * nfold);
        for (int j = 0; j < jbInt; j++)
        {
            int kk   = j % (4 * NBlk);
            int kOfs = kk * s0Cols;
            int jOfs = j  * s0Cols;
            for (int i = 0; i < nfft; i++)
                fs0[kOfs + i] += s0Flat[jOfs + i];
        }

        double refVal = nfold * 4.0;

        // ── Costas sync search ────────────────────────────────────────────────
        int iaS = Math.Max(0,        i0 - (int)(dfTol / df));
        int ibS = Math.Min(nfft - 3, i0 + (int)(dfTol / df));

        double smaxC = 0.0;
        ipk = 0; jpk = 0;
        for (int j = 0; j < 4 * NBlk; j++)
        {
            for (int fi = iaS; fi <= ibS; fi++)
            {
                double ss = 0.0;
                for (int nn = 0; nn < 4; nn++)
                {
                    int kk  = (j + 4 * nn) % (4 * NBlk);
                    int fi2 = fi + 2 * Icos[nn];
                    if ((uint)fi2 < (uint)s0Cols)
                        ss += fs0[kk * s0Cols + fi2];
                }
                if (ss > smaxC) { smaxC = ss; ipk = fi; jpk = j; }
            }
        }

        double ratio = refVal > 0 ? smaxC / refVal : 0.0;
        xsync = ratio - 1.0;
        if (nfold < 26) xsync *= Math.Sqrt(nfold / 26.0);
        xsync -= 0.5; // empirical correction (MSHV)

        sig = SignalMath.Db(Math.Max(ratio - 1.0, 1e-30)) - 15.0;
        if (_mode4 == 1) sig -= 5.0; // ISCAT-A correction

        ndf0 = (int)(df * (ipk - i0));

        // ── Message length estimate from sync-adjacent bins ───────────────────
        smaxC = 0.0;
        int ipk2 = ipk;
        int ja   = (jpk + 15) % (4 * NBlk);
        int jj   = (jpk + 19) % (4 * NBlk);
        for (int fi = ipk; fi <= ipk + 80; fi += 2)
        {
            int fi2 = fi + 10;
            if (fi < s0Cols && fi2 < s0Cols)
            {
                double ss = fs0[ja * s0Cols + fi] + fs0[jj * s0Cols + fi2];
                if (ss > smaxC) { smaxC = ss; ipk2 = fi; }
            }
        }
        msgLen = (ipk2 - ipk) / 2;
        if (msgLen < 2 || msgLen > 39) msgLen = 0;
    }
}

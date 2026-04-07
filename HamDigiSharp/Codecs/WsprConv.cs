using System.Buffers;

namespace HamDigiSharp.Codecs;

/// <summary>
/// WSPR convolutional encoder, Fano sequential decoder, and OSD (Ordered Statistics Decoding)
/// fallback decoder.  K=32 convolutional code + K=50 OSD over the same (162, 50) linear code.
/// Mirrors wsprcode.f90 / wsprd.c / osdwspr.f90 from WSJT-X (K1JT / K9AN, GPL).
/// </summary>
internal static class WsprConv
{
    // ── Convolutional codec constants ─────────────────────────────────────────

    private const int K         = 32;
    private const uint NPoly1   = 0xF2D05351u; // generator 1
    private const uint NPoly2   = 0xE4613C47u; // generator 2

    internal const int NBits    = 81;   // 50 message + 31 tail bits
    private  const int NFano    = 81;   // bits fed into the Fano decoder
    private  const int Ndelta   = 50;   // Fano threshold step (wsprcode.f90 value)
    private  const int MaxCycles = 10000;

    // ── WSPR sync vector ──────────────────────────────────────────────────────

    /// <summary>
    /// WSPR 162-symbol pseudo-random synchronisation vector (wspr_params.f90).
    /// Shared by encoder, decoder, and OSD channel-symbol generation.
    /// </summary>
    internal static readonly byte[] SyncVector =
    {
        1,1,0,0,0,0,0,0,1,0, 0,0,1,1,1,0,0,0,1,0,
        0,1,0,1,1,1,1,0,0,0, 0,0,0,0,1,0,0,1,0,1,
        0,0,0,0,0,0,1,0,1,1, 0,0,1,1,0,1,0,0,0,1,
        1,0,1,0,0,0,0,1,1,0, 1,0,1,0,1,0,1,0,0,1,
        0,0,1,0,1,1,0,0,0,1, 1,0,1,0,1,0,0,0,1,0,
        0,0,0,0,1,0,0,1,0,0, 1,1,1,0,1,1,0,0,1,1,
        0,1,0,0,0,1,1,1,0,0, 0,0,0,1,0,1,0,0,1,1,
        0,0,0,0,0,0,0,1,1,0, 1,0,1,1,0,0,0,1,1,0,
        0,0
    };

    // ── OSD constants ─────────────────────────────────────────────────────────

    private const int OsdK  = 50;   // information bits in the (162,50) code
    private const int OsdN  = 162;  // codeword length
    private const int OsdNt = 66;   // parity symbols checked in pre-screen

    // Two-polynomial generator coefficients for the (162,50) WSPR code
    // (gg array from osdwspr.f90).
    private static readonly byte[] OsdGg =
    {
        1,1,0,1,0,1,0,0,1,0,0,0,1,1,0,0,1,0,1,0,0,1,0,1,1,1,0,1,1,0,0,0,
        0,1,0,0,0,0,0,0,1,0,0,1,1,1,1,0,0,0,1,0,0,1,0,0,1,0,1,1,1,1,1,1
    };

    // OsdGen[row, col] — the (50 × 162) generator matrix.
    // Row 0 = OsdGg zero-padded to 162.  Row i = Row i-1 cyclically right-shifted by 2.
    // (Mirrors osdwspr.f90: gen(i,:) = cshift(gen(i-1,:), -2).)
    private static readonly byte[,] OsdGen = BuildOsdGen();

    private static byte[,] BuildOsdGen()
    {
        var gen = new byte[OsdK, OsdN];
        for (int j = 0; j < 64; j++) gen[0, j] = OsdGg[j];
        for (int i = 1; i < OsdK; i++)
            for (int j = 0; j < OsdN; j++)
                gen[i, j] = gen[i - 1, (j - 2 + OsdN) % OsdN];
        return gen;
    }

    // ── Parity table (popcount mod 2) ─────────────────────────────────────────

    private static readonly byte[] ParTab = BuildParTab();

    private static byte[] BuildParTab()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int v = i, cnt = 0;
            while (v != 0) { cnt ^= v & 1; v >>= 1; }
            t[i] = (byte)cnt;
        }
        return t;
    }

    // ── Interleave table (bit-reversal of 8-bit index, keep values ≤ 161) ────

    private static readonly byte[] IntTab = BuildIntTab();

    private static byte[] BuildIntTab()
    {
        var tab = new byte[162];
        int k = 0;
        for (int i = 0; i < 256 && k < 162; i++)
        {
            int n = 0, ii = i;
            for (int j = 0; j < 8; j++)
            {
                n = n * 2 + (ii & 1);
                ii >>= 1;
            }
            if (n <= 161)
                tab[k++] = (byte)n;
        }
        return tab;
    }

    // ── Convolutional encoder ─────────────────────────────────────────────────

    /// <summary>
    /// Convolutionally encodes 7 bytes (50 message bits + 31 tail zero bits = 81 bits)
    /// into 162 hard-decision symbols (0 or 1).
    /// </summary>
    internal static byte[] Encode(byte[] dat)
    {
        var symbols = new byte[162];
        uint nstate = 0;

        for (int bitIdx = 0; bitIdx < NBits; bitIdx++)
        {
            int byteIdx = bitIdx / 8;
            int bitPos  = 7 - (bitIdx % 8); // MSB first
            uint bit = byteIdx < dat.Length ? (uint)((dat[byteIdx] >> bitPos) & 1) : 0u;

            nstate = (nstate << 1) | bit;

            uint n = nstate & NPoly1;
            n ^= n >> 16;
            symbols[2 * bitIdx]     = ParTab[(n ^ (n >> 8)) & 0xFF];

            n = nstate & NPoly2;
            n ^= n >> 16;
            symbols[2 * bitIdx + 1] = ParTab[(n ^ (n >> 8)) & 0xFF];
        }
        return symbols;
    }

    // ── Interleaving ──────────────────────────────────────────────────────────

    /// <summary>Applies the bit-reversal interleave in place.</summary>
    internal static void Interleave(byte[] symbols)
    {
        Span<byte> tmp = stackalloc byte[162];
        for (int i = 0; i < 162; i++)
            tmp[IntTab[i]] = symbols[i];
        tmp.CopyTo(symbols);
    }

    /// <summary>Reverses the bit-reversal interleave in place.</summary>
    internal static void Deinterleave(byte[] symbols)
    {
        Span<byte> tmp = stackalloc byte[162];
        for (int i = 0; i < 162; i++)
            tmp[i] = symbols[IntTab[i]];
        tmp.CopyTo(symbols);
    }

    // ── Fano metric table ─────────────────────────────────────────────────────

    // Log-likelihood metric for a soft symbol (0-255) when the expected hard bit is 0.
    // MetTab(sym, bit=0) = MetTab0[sym]
    // MetTab(sym, bit=1) = MetTab0[255 - sym]
    private static readonly sbyte[] MetTab0 =
    {
        // 0-78: 5
         5, 5, 5, 5, 5, 5, 5, 5, 5, 5,  5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
         5, 5, 5, 5, 5, 5, 5, 5, 5, 5,  5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
         5, 5, 5, 5, 5, 5, 5, 5, 5, 5,  5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
         5, 5, 5, 5, 5, 5, 5, 5, 5, 5,  5, 5, 5, 5, 5, 5, 5, 5, 5,
        // 79-99: 4
         4, 4, 4, 4, 4, 4, 4, 4, 4, 4,  4, 4, 4, 4, 4, 4, 4, 4, 4, 4,  4,
        // 100-108: 3
         3, 3, 3, 3, 3, 3, 3, 3, 3,
        // 109-113: 2
         2, 2, 2, 2, 2,
        // 114-117: 1
         1, 1, 1, 1,
        // 118-119: 0
         0, 0,
        // 120-122: -1
        -1,-1,-1,
        // 123-124: -2
        -2,-2,
        // 125: -3
        -3,
        // 126-127: -4
        -4,-4,
        // 128: -5
        -5,
        // 129: -6
        -6,
        // 130-131: -7
        -7,-7,
        // 132: -8
        -8,
        // 133: -9
        -9,
        // 134: -10
        -10,
        // 135: -11
        -11,
        // 136-137: -12
        -12,-12,
        // 138: -13
        -13,
        // 139: -14
        -14,
        // 140-149
        -15,-16,-17,-17,-18,-19,-20,-21,-22,-22,
        // 150-159
        -23,-24,-25,-26,-26,-27,-28,-29,-30,-30,
        // 160-169
        -31,-32,-33,-33,-34,-35,-36,-36,-37,-38,
        // 170-179
        -38,-39,-40,-41,-41,-42,-43,-43,-44,-45,
        // 180-189
        -45,-46,-47,-47,-48,-49,-49,-50,-51,-51,
        // 190-199
        -52,-53,-53,-54,-54,-55,-56,-56,-57,-57,
        // 200-209
        -58,-59,-59,-60,-60,-61,-62,-62,-62,-63,
        // 210-219
        -64,-64,-65,-65,-66,-67,-67,-67,-68,-69,
        // 220-229
        -69,-70,-70,-71,-72,-72,-72,-72,-73,-74,
        // 230-239
        -75,-75,-75,-77,-76,-76,-78,-78,-80,-81,
        // 240-249
        -80,-79,-83,-82,-81,-82,-82,-83,-84,-84,
        // 250-255
        -84,-87,-86,-87,-88,-89,
    };

    private static int MetTab(int sym, int bit) =>
        bit == 0 ? MetTab0[sym] : MetTab0[255 - sym];

    // ── Encoder state → expected output symbol pair ───────────────────────────

    // Both generator polynomials have LSB = 1, so flipping the input bit flips
    // both output bits: lsym(bit=1) = lsym(bit=0) ^ 3.
    private static int ComputeLsym(int state)
    {
        uint n = (uint)state & NPoly1;
        n ^= n >> 16;
        int lsym = ParTab[(n ^ (n >> 8)) & 0xFF];

        n = (uint)state & NPoly2;
        n ^= n >> 16;
        lsym = lsym * 2 + ParTab[(n ^ (n >> 8)) & 0xFF];

        return lsym;
    }

    // ── Fano sequential decoder ───────────────────────────────────────────────

    /// <summary>
    /// Decodes 162 soft symbols (0-255, where 0=certain-0 and 255=certain-1)
    /// back to 11 bytes (first 7 bytes carry the 50 WSPR message bits).
    /// Returns false if the decoder fails to find a valid path.
    /// </summary>
    internal static bool FanoDecode(byte[] softSymbols, out byte[] decoded)
    {
        const int MaxBits = 103;
        const int ntail   = NFano - 31; // 50: the first 50 bits are data, 31 are forced-zero tail

        var nstate  = new int[MaxBits];
        var gamma   = new int[MaxBits];
        var metrics = new int[4, MaxBits];
        var tm      = new int[2, MaxBits];
        var ii      = new int[MaxBits];

        decoded = new byte[11];
        for (int pos = 0; pos < NFano; pos++)
        {
            int sa = softSymbols[2 * pos];
            int sb = softSymbols[2 * pos + 1];
            metrics[0, pos] = MetTab(sa, 0) + MetTab(sb, 0); // expected 00
            metrics[1, pos] = MetTab(sa, 0) + MetTab(sb, 1); // expected 01
            metrics[2, pos] = MetTab(sa, 1) + MetTab(sb, 0); // expected 10
            metrics[3, pos] = MetTab(sa, 1) + MetTab(sb, 1); // expected 11
        }

        // Initialise root node (np = 0)
        nstate[0] = 0;
        int lsym = ComputeLsym(nstate[0]);
        int m0   = metrics[lsym,     0];
        int m1   = metrics[lsym ^ 3, 0];
        if (m0 >= m1) { tm[0, 0] = m0; tm[1, 0] = m1; }
        else          { tm[0, 0] = m1; tm[1, 0] = m0; nstate[0] |= 1; }
        ii[0]    = 0;
        gamma[0] = 0;

        int nt = 0; // running Fano threshold
        int np = 0;

        for (int iter = 0; iter < NFano * MaxCycles; iter++)
        {
            // ── Try to advance ────────────────────────────────────────────────
            int ngamma = gamma[np] + tm[ii[np], np];
            if (ngamma >= nt)
            {
                // Tighten threshold to nearest multiple of Ndelta above current
                if (gamma[np] < nt + Ndelta)
                    nt += Ndelta * ((ngamma - nt) / Ndelta);

                gamma[np + 1]  = ngamma;
                nstate[np + 1] = nstate[np] << 1; // low bit may be set below
                np++;

                if (np == NFano - 1)
                    goto done;

                lsym = ComputeLsym(nstate[np]);

                if (np >= ntail)
                {
                    // Tail bits must be 0 — only one branch allowed
                    tm[0, np] = metrics[lsym, np];
                }
                else
                {
                    m0 = metrics[lsym,     np];
                    m1 = metrics[lsym ^ 3, np];
                    if (m0 >= m1) { tm[0, np] = m0; tm[1, np] = m1; }
                    else          { tm[0, np] = m1; tm[1, np] = m0; nstate[np] |= 1; }
                }
                ii[np] = 0;
                continue;
            }

            // ── Threshold violated ────────────────────────────────────────────
            bool noback = (np == 0) || (gamma[np - 1] < nt);
            if (noback)
            {
                nt -= Ndelta;
                // Reset to best branch at current node
                if (ii[np] != 0) { ii[np] = 0; nstate[np] ^= 1; }
                continue;
            }

            // ── Back up one step ──────────────────────────────────────────────
            np--;
            if (np < ntail && ii[np] != 1)
            {
                // Switch to the second-best branch
                ii[np]++;
                nstate[np] ^= 1;
            }
            // else: tail position or both branches exhausted — keep backing up
            // (next iteration will check this position again; if its metric is
            //  below threshold the noback logic will eventually lower nt)
        }

        return false; // decoder failed

        done:
        // Extract decoded bytes: nstate[7], nstate[15], ..., nstate[79]
        int npx = 7;
        for (int j = 0; j < 10; j++, npx += 8)
            decoded[j] = (byte)(nstate[npx] & 0xFF);
        decoded[10] = 0;
        return true;
    }

    // ── Channel symbols (for signal subtraction) ──────────────────────────────

    /// <summary>
    /// Reproduces the 162 4-FSK channel symbols {0..3} for a decoded message.
    /// chanSym[i] = 2 * conv[i] + SyncVector[i], where conv[] is the
    /// convolutional encoding of <paramref name="dat"/>, interleaved.
    /// Used to subtract a decoded signal from the baseband.
    /// </summary>
    internal static byte[] GetChannelSymbols(byte[] dat)
    {
        var cs = Encode(dat);           // 162 hard-decision symbols 0/1
        Interleave(cs);                 // bit-reversal interleave
        var chan = new byte[OsdN];
        for (int i = 0; i < OsdN; i++)
            chan[i] = (byte)(2 * cs[i] + SyncVector[i]); // 0..3
        return chan;
    }

    // ── OSD (Ordered Statistics Decoding) fallback ────────────────────────────

    /// <summary>
    /// Attempts to decode 162 <b>deinterleaved</b> soft symbols (0 = certain-0,
    /// 255 = certain-1) using Ordered Statistics Decoding (OSD).
    ///
    /// Port of <c>osdwspr.f90</c> (K9AN, GPL).  Uses the (162, 50) convolutional
    /// code generator matrix to find the minimum weighted-Hamming-distance valid
    /// codeword, then runs <see cref="FanoDecode"/> on it to extract the payload.
    ///
    /// Algorithm:
    /// <list type="number">
    ///   <item>Sort columns by descending |soft| reliability.</item>
    ///   <item>GF(2) Gaussian elimination → systematic form (identity in first K=50 cols).</item>
    ///   <item>Encode the K most-reliable hard decisions (order-0 candidate).</item>
    ///   <item>Test all order-1 single-bit perturbations; accept if pre-screen passes.</item>
    ///   <item>If <paramref name="depth"/> ≥ 2, also test all C(K,2) order-2 two-bit flips.</item>
    ///   <item>Return the codeword with the smallest weighted distance via Fano.</item>
    /// </list>
    ///
    /// <paramref name="depth"/> trade-offs:
    /// <list type="bullet">
    ///   <item><b>1</b> — K+1 = 51 candidates, pre-screened (ntheta=16).  &lt;5 ms per call.  Recommended for real-time use.</item>
    ///   <item><b>2</b> — adds C(K,2) ≈ 1225 candidates, pre-screened (ntheta=22).  ≈50 ms per call.  Suitable for offline/archive.</item>
    /// </list>
    ///
    /// Returns false if no valid codeword is found or FanoDecode fails on the best candidate.
    /// </summary>
    internal static bool OsdDecode(byte[] softSymbols, int depth, out byte[] decoded)
    {
        const int N = OsdN, K = OsdK, Nt = OsdNt;

        decoded = Array.Empty<byte>();

        // ── 1. Normalize and hard-decide ──────────────────────────────────────
        Span<float> rx     = stackalloc float[N];
        Span<byte>  hdec   = stackalloc byte[N];
        Span<float> absrx  = stackalloc float[N];

        for (int i = 0; i < N; i++)
        {
            rx[i]   = (softSymbols[i] - 128.0f) / 127.0f;
            hdec[i] = rx[i] >= 0 ? (byte)1 : (byte)0;
            absrx[i] = MathF.Abs(rx[i]);
        }

        // ── 2. Sort indices by DESCENDING reliability |rx| ────────────────────
        Span<int> indices = stackalloc int[N];
        for (int i = 0; i < N; i++) indices[i] = i;

        // Insertion sort (162 elements — fast enough, avoids allocation)
        for (int i = 1; i < N; i++)
        {
            int  ki  = indices[i];
            float kv = absrx[ki];
            int j = i - 1;
            while (j >= 0 && absrx[indices[j]] < kv) { indices[j + 1] = indices[j]; j--; }
            indices[j + 1] = ki;
        }

        // ── 3. Build genmrb: columns of OsdGen reordered by reliability ───────
        // Use a rented flat array [row * N + col] for cache-friendly row scans.
        byte[] genmrbArr = ArrayPool<byte>.Shared.Rent(K * N);
        try
        {
            // Initialise from static generator matrix with column permutation
            for (int r = 0; r < K; r++)
                for (int c = 0; c < N; c++)
                    genmrbArr[r * N + c] = OsdGen[r, indices[c]];

            // ── 4. GF(2) Gaussian elimination — identity in first K columns ──────
            for (int k = 0; k < K; k++)
            {
                // Find pivot: first column ≥ k with a 1 in row k
                int pivot = -1;
                for (int c = k; c < N; c++)
                {
                    if (genmrbArr[k * N + c] == 1) { pivot = c; break; }
                }
                if (pivot < 0) continue; // linearly dependent row (shouldn't happen)

                // Swap columns k ↔ pivot (for all rows), update indices
                if (pivot != k)
                {
                    for (int r = 0; r < K; r++)
                        (genmrbArr[r * N + k], genmrbArr[r * N + pivot]) =
                        (genmrbArr[r * N + pivot], genmrbArr[r * N + k]);
                    (indices[k], indices[pivot]) = (indices[pivot], indices[k]);
                }

                // Eliminate column k from all other rows (Gauss-Jordan)
                for (int r = 0; r < K; r++)
                {
                    if (r != k && genmrbArr[r * N + k] == 1)
                    {
                        for (int c = 0; c < N; c++)
                            genmrbArr[r * N + c] ^= genmrbArr[k * N + c];
                    }
                }
            }

            // ── 5. Reorder hdec and absrx by final column permutation ─────────────
            Span<byte>  hdecR  = stackalloc byte[N];
            Span<float> absrxR = stackalloc float[N];
            for (int i = 0; i < N; i++)
            {
                hdecR[i]  = hdec[indices[i]];
                absrxR[i] = absrx[indices[i]];
            }

            // ── 6. Order-0: encode the K MRB hard decisions ───────────────────────
            Span<byte> m0     = stackalloc byte[K];
            Span<byte> me     = stackalloc byte[K];
            Span<byte> ce     = stackalloc byte[N];
            Span<byte> bestCw = stackalloc byte[N];

            hdecR[..K].CopyTo(m0);
            OsdMrbEncode(m0, ce, genmrbArr, K, N);

            float dmin = 0;
            for (int i = 0; i < N; i++) dmin += (ce[i] ^ hdecR[i]) * absrxR[i];
            ce.CopyTo(bestCw);

            // Pre-screen thresholds (osdwspr.f90 §102-136)
            int ntheta = depth <= 1 ? 16 : 22;

            // ── 7. Order-1 perturbations (all K single-bit flips) ─────────────────
            for (int b1 = 0; b1 < K; b1++)
            {
                m0.CopyTo(me);
                me[b1] ^= 1;

                OsdMrbEncode(me, ce, genmrbArr, K, N);

                // Pre-screen: parity errors in first Nt of the N-K parity positions
                int ndKpt = 0;
                for (int i = K; i < K + Nt; i++) ndKpt += ce[i] ^ hdecR[i];
                if (ndKpt + 1 > ntheta) continue;

                float dd = 0;
                for (int i = 0; i < N; i++) dd += (ce[i] ^ hdecR[i]) * absrxR[i];
                if (dd < dmin) { dmin = dd; ce.CopyTo(bestCw); }
            }

            // ── 8. Order-2 perturbations (all C(K,2) two-bit flips) if depth ≥ 2 ─
            if (depth >= 2)
            {
                for (int b1 = 1; b1 < K; b1++)
                for (int b2 = 0; b2 < b1; b2++)
                {
                    m0.CopyTo(me);
                    me[b1] ^= 1;
                    me[b2] ^= 1;

                    OsdMrbEncode(me, ce, genmrbArr, K, N);

                    int ndKpt = 0;
                    for (int i = K; i < K + Nt; i++) ndKpt += ce[i] ^ hdecR[i];
                    if (ndKpt + 2 > ntheta) continue;

                    float dd = 0;
                    for (int i = 0; i < N; i++) dd += (ce[i] ^ hdecR[i]) * absrxR[i];
                    if (dd < dmin) { dmin = dd; ce.CopyTo(bestCw); }
                }
            }

            // ── 9. Remap winning codeword back to original column order ───────────
            var cwFull  = new byte[N];
            var softCw  = new byte[N];
            for (int i = 0; i < N; i++) cwFull[indices[i]] = bestCw[i];
            for (int i = 0; i < N; i++) softCw[i]          = (byte)(255 * cwFull[i]);

            // ── 10. Run Fano on the 0/255 hard codeword — should always succeed ────
            return FanoDecode(softCw, out decoded);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(genmrbArr);
        }
    }

    /// <summary>
    /// Encodes K-bit message <paramref name="me"/> using the modified generator matrix
    /// <paramref name="genmrb"/> (flat row-major, K rows × N columns) into codeword
    /// <paramref name="cw"/> of length N.  GF(2) arithmetic (XOR).
    /// </summary>
    private static void OsdMrbEncode(ReadOnlySpan<byte> me, Span<byte> cw,
                                     byte[] genmrb, int rowK, int colN)
    {
        cw.Clear();
        for (int i = 0; i < rowK; i++)
        {
            if (me[i] == 0) continue;
            int rowOff = i * colN;
            for (int j = 0; j < colN; j++)
                cw[j] ^= genmrb[rowOff + j];
        }
    }
}

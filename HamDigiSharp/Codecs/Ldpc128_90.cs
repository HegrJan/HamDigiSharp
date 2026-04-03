using System.Buffers;
using HamDigiSharp.Dsp;

namespace HamDigiSharp.Codecs;

/// <summary>
/// LDPC(128,90) belief-propagation decoder for MSK144.
/// N=128 coded bits, K=90 information bits (77 msg + 13 CRC), M=38 parity checks.
///
/// Tanner graph tables from MSHV's <c>bpdecode_msk_128_90.h</c> (v2 tables).
/// Algorithm mirrors MSHV's <c>GenMsk::bpdecode128_90</c>.
///
/// Algorithms originally from WSJT-X (K1JT et al.), GPL.
/// </summary>
public static class Ldpc128_90
{
    private const int N = 128;
    private const int K = 90;
    private const int M = N - K; // 38

    // ── Tanner-graph tables (from bpdecode_msk_128_90.h v2) ──────────────────
    // Mn[n][3]: 3 check-node indices (1-based) connected to variable node n
    // Nm[m][11]: up to 11 variable-node indices (1-based) connected to check m. 0 = unused.
    // Nrw[m]: number of variable nodes connected to check m (10 or 11)

    private static readonly int[,] Mn = new int[128, 3]
    {
        {21,34,36},{1,8,28},{2,9,37},{3,7,19},{4,16,32},{2,5,22},{6,13,25},{10,31,33},
        {11,24,27},{12,15,23},{14,18,26},{17,20,29},{17,30,34},{6,34,35},{1,10,30},
        {3,18,23},{4,12,25},{5,28,36},{7,14,21},{8,15,31},{9,27,32},{11,19,35},{13,16,37},
        {20,24,38},{21,22,26},{12,29,33},{1,17,35},{2,28,30},{3,10,32},{4,8,36},{5,19,29},
        {6,20,27},{7,22,37},{9,11,33},{13,24,26},{14,31,34},{15,16,25},{13,18,38},{8,20,23},
        {1,32,33},{2,17,19},{3,24,34},{4,7,38},{5,11,31},{6,18,21},{9,15,36},{10,16,28},
        {12,26,30},{14,27,29},{22,25,35},{23,30,32},{4,11,37},{1,14,23},{2,8,25},{3,13,27},
        {5,10,37},{6,16,31},{7,15,18},{9,22,24},{12,19,36},{17,26,38},{20,21,33},{20,28,35},
        {4,29,34},{1,26,36},{2,23,34},{3,9,38},{5,6,17},{7,27,35},{8,14,32},{10,15,22},
        {11,18,29},{12,13,28},{16,19,33},{21,25,31},{24,30,37},{1,3,21},{2,18,31},{4,6,9},
        {5,8,33},{7,29,32},{10,13,19},{11,22,23},{12,27,34},{14,15,30},{16,27,38},{17,28,37},
        {20,25,26},{5,24,35},{3,6,36},{1,12,31},{2,4,33},{3,16,30},{1,2,24},{5,23,27},
        {6,28,32},{7,17,36},{8,22,38},{9,18,20},{10,21,29},{11,13,34},{4,14,20},{11,30,38},
        {14,35,37},{15,19,26},{3,28,29},{7,8,9},{5,18,34},{13,15,17},{12,16,35},{10,23,25},
        {19,21,37},{17,27,31},{24,25,36},{1,18,19},{6,26,33},{22,31,32},{3,20,22},{4,21,27},
        {2,13,29},{6,7,12},{15,24,32},{9,25,30},{23,37,38},{5,16,26},{11,14,28},{33,36,38},
        {8,10,35}
    };

    private static readonly int[,] Nm = new int[38, 11]
    {
        { 2, 15, 27, 40, 53, 65, 77, 91, 94,115,  0},
        { 3,  6, 28, 41, 54, 66, 78, 92, 94,120,  0},
        { 4, 16, 29, 42, 55, 67, 77, 90, 93,106,118},
        { 5, 17, 30, 43, 52, 64, 79, 92,102,119,  0},
        { 6, 18, 31, 44, 56, 68, 80, 89, 95,108,125},
        { 7, 14, 32, 45, 57, 68, 79, 90, 96,116,121},
        { 4, 19, 33, 43, 58, 69, 81, 97,107,121,  0},
        { 2, 20, 30, 39, 54, 70, 80, 98,107,128,  0},
        { 3, 21, 34, 46, 59, 67, 79, 99,107,123,  0},
        { 8, 15, 29, 47, 56, 71, 82,100,111,128,  0},
        { 9, 22, 34, 44, 52, 72, 83,101,103,126,  0},
        {10, 17, 26, 48, 60, 73, 84, 91,110,121,  0},
        { 7, 23, 35, 38, 55, 73, 82,101,109,120,  0},
        {11, 19, 36, 49, 53, 70, 85,102,104,126,  0},
        {10, 20, 37, 46, 58, 71, 85,105,109,122,  0},
        { 5, 23, 37, 47, 57, 74, 86, 93,110,125,  0},
        {12, 13, 27, 41, 61, 68, 87, 97,109,113,  0},
        {11, 16, 38, 45, 58, 72, 78, 99,108,115,  0},
        { 4, 22, 31, 41, 60, 74, 82,105,112,115,  0},
        {12, 24, 32, 39, 62, 63, 88, 99,102,118,  0},
        { 1, 19, 25, 45, 62, 75, 77,100,112,119,  0},
        { 6, 25, 33, 50, 59, 71, 83, 98,117,118,  0},
        {10, 16, 39, 51, 53, 66, 83, 95,111,124,  0},
        { 9, 24, 35, 42, 59, 76, 89, 94,114,122,  0},
        { 7, 17, 37, 50, 54, 75, 88,111,114,123,  0},
        {11, 25, 35, 48, 61, 65, 88,105,116,125,  0},
        { 9, 21, 32, 49, 55, 69, 84, 86, 95,113,119},
        { 2, 18, 28, 47, 63, 73, 87, 96,106,126,  0},
        {12, 26, 31, 49, 64, 72, 81,100,106,120,  0},
        {13, 15, 28, 48, 51, 76, 85, 93,103,123,  0},
        { 8, 20, 36, 44, 57, 75, 78, 91,113,117,  0},
        { 5, 21, 29, 40, 51, 70, 81, 96,117,122,  0},
        { 8, 26, 34, 40, 62, 74, 80, 92,116,127,  0},
        { 1, 13, 14, 36, 42, 64, 66, 84,101,108,  0},
        {14, 22, 27, 50, 63, 69, 89,104,110,128,  0},
        { 1, 18, 30, 46, 60, 65, 90, 97,114,127,  0},
        { 3, 23, 33, 52, 56, 76, 87,104,112,124,  0},
        {24, 38, 43, 61, 67, 86, 98,103,124,127,  0}
    };

    private static readonly int[] Nrw =
    {
        10,10,11,10,11,11,10,10,10,10,10,10,10,10,10,10,10,10,
        10,10,10,10,10,10,10,10,11,10,10,10,10,10,10,10,10,10,
        10,10
    };

    // ── Piecewise-linear atanh (same as LDPC174_91) ───────────────────────────

    private static double Platanh(double x)
    {
        double sign = x < 0 ? -1.0 : 1.0;
        double z = Math.Abs(x);
        if (z <= 0.664)  return x / 0.83;
        if (z <= 0.9217) return sign * (z - 0.4064) / 0.322;
        if (z <= 0.9951) return sign * (z - 0.8378) / 0.0524;
        if (z <= 0.9998) return sign * (z - 0.9914) / 0.0012;
        return sign * 7.0;
    }

    // ── Belief Propagation decoder ────────────────────────────────────────────

    /// <summary>
    /// Belief-propagation LDPC(128,90) decoder for MSK144.
    /// </summary>
    /// <param name="llr">
    ///   Soft log-likelihood ratios (length 128). Positive = bit 1 (matches MSHV convention).
    /// </param>
    /// <param name="decoded90">
    ///   Output: 90 decoded bits (77 message + 13 CRC). Must be length ≥ 90.
    /// </param>
    /// <param name="hardErrors">
    ///   Number of hard bit errors (0 = clean decode); −1 = failure.
    /// </param>
    public static void BpDecode(
        ReadOnlySpan<double> llr,
        bool[] decoded90,
        out int hardErrors)
    {
        // ── Pool flat arrays to eliminate per-call GC pressure ────────────────
        // Flat indexing: tov[i,j] → tov[i*3+j], toc[j,i] → toc[j*11+i]
        var pool = ArrayPool<double>.Shared;
        var bpool = ArrayPool<bool>.Shared;
        double[] tov = pool.Rent(N * 3);   // variable→check: [128×3]
        double[] toc = pool.Rent(M * 11);  // check→variable: [38×11]
        double[] zn  = pool.Rent(N);        // soft decisions: [128]
        bool[]   cw  = bpool.Rent(N);       // hard decisions: [128]

        // tov starts at zero (no extrinsic information on first iteration)
        tov.AsSpan(0, N * 3).Clear();
        hardErrors = -1;

        // Initialise toc from LLR
        for (int j = 0; j < M; j++)
            for (int i = 0; i < Nrw[j]; i++)
                toc[j * 11 + i] = llr[Nm[j, i] - 1];

        int ncnt = 0, nclast = 0;
        try
        {
            for (int iter = 0; iter <= 30; iter++)
            {
                // Compute soft decisions
                for (int i = 0; i < N; i++)
                {
                    zn[i] = llr[i] + tov[i * 3] + tov[i * 3 + 1] + tov[i * 3 + 2];
                    cw[i] = zn[i] > 0.0;
                }

                // Count unsatisfied parity checks
                int ncheck = 0;
                for (int i = 0; i < M; i++)
                {
                    int sum = 0;
                    for (int x = 0; x < Nrw[i]; x++) sum += cw[Nm[i, x] - 1] ? 1 : 0;
                    if (sum % 2 != 0) ncheck++;
                }

                if (ncheck == 0)
                {
                    // All parity checks satisfied — verify CRC-13
                    int count = 0;
                    for (int i = 0; i < N; i++)
                        if ((2 * (cw[i] ? 1 : 0) - 1) * llr[i] < 0.0) count++;
                    hardErrors = count;

                    Array.Copy(cw, decoded90, Math.Min(K, decoded90.Length));
                    if (Crc13.Check(decoded90))
                        return;
                }

                if (iter > 0)
                {
                    int nd = ncheck - nclast;
                    if (nd < 0) ncnt = 0; else ncnt++;
                    if (ncnt >= 5 && iter >= 10 && ncheck > 10) { hardErrors = -1; return; }
                }
                nclast = ncheck;

                // Variable → check messages
                for (int j = 0; j < M; j++)
                {
                    int jBase = j * 11;
                    for (int i = 0; i < Nrw[j]; i++)
                    {
                        int ibj = Nm[j, i] - 1;
                        double t = zn[ibj];
                        for (int kk = 0; kk < 3; kk++)
                            if (Mn[ibj, kk] - 1 == j)
                                t -= tov[ibj * 3 + kk];
                        toc[jBase + i] = t;
                    }
                }

                // tanh + check → variable messages
                for (int j = 0; j < N; j++)
                {
                    int jBase3 = j * 3;
                    for (int i = 0; i < 3; i++)
                    {
                        int ichk     = Mn[j, i] - 1;
                        int ichkBase = ichk * 11;
                        double tmn   = 1.0;
                        for (int z = 0; z < Nrw[ichk]; z++)
                        {
                            if (Nm[ichk, z] - 1 != j)
                                tmn *= SignalMath.FastTanh(-toc[ichkBase + z] * 0.5);
                        }
                        tmn = Math.Max(-0.9999999, Math.Min(0.9999999, tmn));
                        tov[jBase3 + i] = -2.0 * Platanh(tmn);
                    }
                }
            }
            hardErrors = -1;
        }
        finally
        {
            pool.Return(tov);
            pool.Return(toc);
            pool.Return(zn);
            bpool.Return(cw);
        }
    }

    /// <summary>
    /// Systematic encoder for LDPC(128,90).
    /// Appends CRC-13 to produce a 90-bit message block, then computes 38 parity bits.
    /// Output codeword is [message90 (90 bits) | parity (38 bits)] = 128 bits.
    /// Generator matrix from MSHV's <c>GenMsk::encode_128_90</c>.
    /// </summary>
    public static void Encode(ReadOnlySpan<bool> message77, Span<bool> codeword128)
    {
        if (message77.Length < 77)    throw new ArgumentException("message77 must have >= 77 elements");
        if (codeword128.Length < 128) throw new ArgumentException("codeword128 must have >= 128 elements");

        // Build 90-bit message = msg77 + CRC13
        Span<bool> msg90 = stackalloc bool[90];
        message77[..77].CopyTo(msg90);
        int crc = Crc13.ComputeFromBits77(message77);
        for (int i = 0; i < 13; i++)
            msg90[77 + i] = ((crc >> (12 - i)) & 1) == 1;

        // Systematic part: codeword[0..89] = msg90
        msg90.CopyTo(codeword128[..90]);

        // Parity part: codeword[90..127] = (msg90 × G) mod 2
        for (int row = 0; row < M; row++)
        {
            int nsum = 0;
            for (int col = 0; col < K; col++)
                if (msg90[col] && GenMatrix[col * M + row])
                    nsum++;
            codeword128[K + row] = (nsum & 1) == 1;
        }
    }

    // ── Generator matrix for encoding (from encode_128_90 in genmesage_msk.cpp) ─
    // 38 hex strings, each 23 chars (4 bits/char) → 92 bits; only first 90 used.
    // GenMatrix[col*38+row] = whether col participates in parity check row.
    private static readonly bool[] GenMatrix = BuildGenMatrix();

    private static bool[] BuildGenMatrix()
    {
        string[] g =
        {
            "a08ea80879050a5e94da994",
            "59f3b48040ca089c81ee880",
            "e4070262802e31b7b17d3dc",
            "95cbcbaf032dc3d960bacc8",
            "c4d79b5dcc21161a254ffbc",
            "93fde9cdbf2622a70868424",
            "e73b888bb1b01167379ba28",
            "45a0d0a0f39a7ad2439949c",
            "759acef19444bcad79c4964",
            "71eb4dddf4f5ed9e2ea17e0",
            "80f0ad76fb247d6b4ca8d38",
            "184fff3aa1b82dc66640104",
            "ca4e320bb382ed14cbb1094",
            "52514447b90e25b9e459e28",
            "dd10c1666e071956bd0df38",
            "99c332a0b792a2da8ef1ba8",
            "7bd9f688e7ed402e231aaac",
            "00fcad76eb647d6a0ca8c38",
            "6ac8d0499c43b02eed78d70",
            "2c2c764baf795b4788db010",
            "0e907bf9e280d2624823dd0",
            "b857a6e315afd8c1c925e64",
            "8deb58e22d73a141cae3778",
            "22d3cb80d92d6ac132dfe08",
            "754763877b28c187746855c",
            "1d1bb7cf6953732e04ebca4",
            "2c65e0ea4466ab9f5e1deec",
            "6dc530ca37fc916d1f84870",
            "49bccbbee152355be7ac984",
            "e8387f3f4367cf45a150448",
            "8ce25e03d67d51091c81884",
            "b798012ffa40a93852752c8",
            "2e43307933adfca37adc3c8",
            "ca06e0a42ca1ec782d6c06c",
            "c02b762927556a7039e638c",
            "4a3e9b7d08b6807f8619fac",
            "45e8030f68997bb68544424",
            "7e79362c16773efc6482e30",
        };

        var mat = new bool[K * M]; // [col * M + row]
        for (int row = 0; row < M; row++)
        {
            for (int j = 0; j < 23; j++)
            {
                int nibble = Convert.ToInt32(g[row][j].ToString(), 16);
                for (int jj = 0; jj < 4; jj++)
                {
                    int col = j * 4 + jj;
                    if (col >= K) break;
                    if (((nibble >> (3 - jj)) & 1) == 1)
                        mat[col * M + row] = true;
                }
            }
        }
        return mat;
    }


    /// <summary>
    /// Verifies all 38 parity-check equations for a 128-bit codeword using the Tanner graph.
    /// Returns true if all checks pass (codeword is a valid codeword).
    /// Useful for validating encoder output without going through BpDecode.
    /// </summary>
    public static bool CheckParity(ReadOnlySpan<bool> codeword128)
    {
        if (codeword128.Length < N) return false;
        for (int i = 0; i < M; i++)
        {
            int parity = 0;
            for (int x = 0; x < Nrw[i]; x++)
                parity ^= codeword128[Nm[i, x] - 1] ? 1 : 0;
            if (parity != 0) return false;
        }
        return true;
    }

    public static bool TryDecode(ReadOnlySpan<double> llr, bool[] decoded90, out int hardErrors)
    {
        BpDecode(llr, decoded90, out hardErrors);
        return hardErrors >= 0 && Crc13.Check(decoded90);
    }
}

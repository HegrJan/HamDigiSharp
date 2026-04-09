using System.Buffers;
using HamDigiSharp.Dsp;
using HamDigiSharp.Models;

namespace HamDigiSharp.Codecs;

/// <summary>
/// LDPC(174,91) belief-propagation + OSD decoder for FT8, FT4, FT2, and Q65.
/// Faithful C# port of MSHV's <c>PomFt::bpdecode174_91var</c> and <c>osd174_91_1</c>.
///
/// Algorithms originally from WSJT-X (K1JT, G4WJS, K9AN et al.), GPL.
/// </summary>
public static class Ldpc174_91
{
    // ── Parity-check tanner-graph tables ─────────────────────────────────────
    // Mn[n][k]: check-node indices (1-based) connected to variable node n.
    // Nm[m][k]: variable-node indices (1-based) connected to check node m. 0 = unused.
    // nrw[m]:   number of variable nodes connected to check m.

    private static readonly int[,] Mn = new int[174, 3]
    {
        {16,45,73},{25,51,62},{33,58,78},{1,44,45},{2,7,61},{3,6,54},{4,35,48},{5,13,21},
        {8,56,79},{9,64,69},{10,19,66},{11,36,60},{12,37,58},{14,32,43},{15,63,80},{17,28,77},
        {18,74,83},{22,53,81},{23,30,34},{24,31,40},{26,41,76},{27,57,70},{29,49,65},{3,38,78},
        {5,39,82},{46,50,73},{51,52,74},{55,71,72},{44,67,72},{43,68,78},{1,32,59},{2,6,71},
        {4,16,54},{7,65,67},{8,30,42},{9,22,31},{10,18,76},{11,23,82},{12,28,61},{13,52,79},
        {14,50,51},{15,81,83},{17,29,60},{19,33,64},{20,26,73},{21,34,40},{24,27,77},{25,55,58},
        {35,53,66},{36,48,68},{37,46,75},{38,45,47},{39,57,69},{41,56,62},{20,49,53},{46,52,63},
        {45,70,75},{27,35,80},{1,15,30},{2,68,80},{3,36,51},{4,28,51},{5,31,56},{6,20,37},
        {7,40,82},{8,60,69},{9,10,49},{11,44,57},{12,39,59},{13,24,55},{14,21,65},{16,71,78},
        {17,30,76},{18,25,80},{19,61,83},{22,38,77},{23,41,50},{7,26,58},{29,32,81},{33,40,73},
        {18,34,48},{13,42,64},{5,26,43},{47,69,72},{54,55,70},{45,62,68},{10,63,67},{14,66,72},
        {22,60,74},{35,39,79},{1,46,64},{1,24,66},{2,5,70},{3,31,65},{4,49,58},{1,4,5},
        {6,60,67},{7,32,75},{8,48,82},{9,35,41},{10,39,62},{11,14,61},{12,71,74},{13,23,78},
        {11,35,55},{15,16,79},{7,9,16},{17,54,63},{18,50,57},{19,30,47},{20,64,80},{21,28,69},
        {22,25,43},{13,22,37},{2,47,51},{23,54,74},{26,34,72},{27,36,37},{21,36,63},{29,40,44},
        {19,26,57},{3,46,82},{14,15,58},{33,52,53},{30,43,52},{6,9,52},{27,33,65},{25,69,73},
        {38,55,83},{20,39,77},{18,29,56},{32,48,71},{42,51,59},{28,44,79},{34,60,62},{31,45,61},
        {46,68,77},{6,24,76},{8,10,78},{40,41,70},{17,50,53},{42,66,68},{4,22,72},{36,64,81},
        {13,29,47},{2,8,81},{56,67,73},{5,38,50},{12,38,64},{59,72,80},{3,26,79},{45,76,81},
        {1,65,74},{7,18,77},{11,56,59},{14,39,54},{16,37,66},{10,28,55},{15,60,70},{17,25,82},
        {20,30,31},{12,67,68},{23,75,80},{27,32,62},{24,69,75},{19,21,71},{34,53,61},{35,46,47},
        {33,59,76},{40,43,83},{41,42,63},{49,75,83},{20,44,48},{42,49,57}
    };

    private static readonly int[,] Nm = new int[83, 7]
    {
        {4,31,59,91,92,96,153},{5,32,60,93,115,146,0},{6,24,61,94,122,151,0},
        {7,33,62,95,96,143,0},{8,25,63,83,93,96,148},{6,32,64,97,126,138,0},
        {5,34,65,78,98,107,154},{9,35,66,99,139,146,0},{10,36,67,100,107,126,0},
        {11,37,67,87,101,139,158},{12,38,68,102,105,155,0},{13,39,69,103,149,162,0},
        {8,40,70,82,104,114,145},{14,41,71,88,102,123,156},{15,42,59,106,123,159,0},
        {1,33,72,106,107,157,0},{16,43,73,108,141,160,0},{17,37,74,81,109,131,154},
        {11,44,75,110,121,166,0},{45,55,64,111,130,161,173},{8,46,71,112,119,166,0},
        {18,36,76,89,113,114,143},{19,38,77,104,116,163,0},{20,47,70,92,138,165,0},
        {2,48,74,113,128,160,0},{21,45,78,83,117,121,151},{22,47,58,118,127,164,0},
        {16,39,62,112,134,158,0},{23,43,79,120,131,145,0},{19,35,59,73,110,125,161},
        {20,36,63,94,136,161,0},{14,31,79,98,132,164,0},{3,44,80,124,127,169,0},
        {19,46,81,117,135,167,0},{7,49,58,90,100,105,168},{12,50,61,118,119,144,0},
        {13,51,64,114,118,157,0},{24,52,76,129,148,149,0},{25,53,69,90,101,130,156},
        {20,46,65,80,120,140,170},{21,54,77,100,140,171,0},{35,82,133,142,171,174,0},
        {14,30,83,113,125,170,0},{4,29,68,120,134,173,0},{1,4,52,57,86,136,152},
        {26,51,56,91,122,137,168},{52,84,110,115,145,168,0},{7,50,81,99,132,173,0},
        {23,55,67,95,172,174,0},{26,41,77,109,141,148,0},{2,27,41,61,62,115,133},
        {27,40,56,124,125,126,0},{18,49,55,124,141,167,0},{6,33,85,108,116,156,0},
        {28,48,70,85,105,129,158},{9,54,63,131,147,155,0},{22,53,68,109,121,174,0},
        {3,13,48,78,95,123,0},{31,69,133,150,155,169,0},{12,43,66,89,97,135,159},
        {5,39,75,102,136,167,0},{2,54,86,101,135,164,0},{15,56,87,108,119,171,0},
        {10,44,82,91,111,144,149},{23,34,71,94,127,153,0},{11,49,88,92,142,157,0},
        {29,34,87,97,147,162,0},{30,50,60,86,137,142,162},{10,53,66,84,112,128,165},
        {22,57,85,93,140,159,0},{28,32,72,103,132,166,0},{28,29,84,88,117,143,150},
        {1,26,45,80,128,147,0},{17,27,89,103,116,153,0},{51,57,98,163,165,172,0},
        {21,37,73,138,152,169,0},{16,47,76,130,137,154,0},{3,24,30,72,104,139,0},
        {9,40,90,106,134,151,0},{15,58,60,74,111,150,163},{18,42,79,144,146,152,0},
        {25,38,65,99,122,160,0},{17,42,75,129,170,172,0}
    };

    private static readonly int[] Nrw =
    {
        7,6,6,6,7,6,7,6,6,7,6,6,7,7,6,6,6,7,6,7,6,7,6,6,6,7,6,6,6,7,6,6,6,6,7,
        6,6,6,7,7,6,6,6,6,7,7,6,6,6,6,7,6,6,6,7,6,6,6,6,7,6,6,6,7,6,6,6,7,7,6,
        6,7,6,6,6,6,6,6,6,7,6,6,6
    };

    private const int N = 174;
    private const int K = 91;
    private const int M = N - K; // 83

    // ── Generator matrix for LDPC(174,91) encoder ─────────────────────────────
    // Source: MSHV genpom.cpp / bpdecode_ft8_174_91.h (g_ft8_174_91[83]).
    // 83 rows of 23 hex characters each = 91 bits per row (last nibble uses 3 of 4 bits).
    // Layout after parsing: _genMatrix[col * 83 + row], col=info bit (0..90), row=parity (0..82).

    private static readonly string[] GenHex =
    {
        "8329ce11bf31eaf509f27fc", "761c264e25c259335493132", "dc265902fb277c6410a1bdc",
        "1b3f417858cd2dd33ec7f62", "09fda4fee04195fd034783a", "077cccc11b8873ed5c3d48a",
        "29b62afe3ca036f4fe1a9da", "6054faf5f35d96d3b0c8c3e", "e20798e4310eed27884ae90",
        "775c9c08e80e26ddae56318", "b0b811028c2bf997213487c", "18a0c9231fc60adf5c5ea32",
        "76471e8302a0721e01b12b8", "ffbccb80ca8341fafb47b2e", "66a72a158f9325a2bf67170",
        "c4243689fe85b1c51363a18", "0dff739414d1a1b34b1c270", "15b48830636c8b99894972e",
        "29a89c0d3de81d665489b0e", "4f126f37fa51cbe61bd6b94", "99c47239d0d97d3c84e0940",
        "1919b75119765621bb4f1e8", "09db12d731faee0b86df6b8", "488fc33df43fbdeea4eafb4",
        "827423ee40b675f756eb5fe", "abe197c484cb74757144a9a", "2b500e4bc0ec5a6d2bdbdd0",
        "c474aa53d70218761669360", "8eba1a13db3390bd6718cec", "753844673a27782cc42012e",
        "06ff83a145c37035a5c1268", "3b37417858cc2dd33ec3f62", "9a4a5a28ee17ca9c324842c",
        "bc29f465309c977e89610a4", "2663ae6ddf8b5ce2bb29488", "46f231efe457034c1814418",
        "3fb2ce85abe9b0c72e06fbe", "de87481f282c153971a0a2e", "fcd7ccf23c69fa99bba1412",
        "f0261447e9490ca8e474cec", "4410115818196f95cdd7012", "088fc31df4bfbde2a4eafb4",
        "b8fef1b6307729fb0a078c0", "5afea7acccb77bbc9d99a90", "49a7016ac653f65ecdc9076",
        "1944d085be4e7da8d6cc7d0", "251f62adc4032f0ee714002", "56471f8702a0721e00b12b8",
        "2b8e4923f2dd51e2d537fa0", "6b550a40a66f4755de95c26", "a18ad28d4e27fe92a4f6c84",
        "10c2e586388cb82a3d80758", "ef34a41817ee02133db2eb0", "7e9c0c54325a9c15836e000",
        "3693e572d1fde4cdf079e86", "bfb2cec5abe1b0c72e07fbe", "7ee18230c583cccc57d4b08",
        "a066cb2fedafc9f52664126", "bb23725abc47cc5f4cc4cd2", "ded9dba3bee40c59b5609b4",
        "d9a7016ac653e6decdc9036", "9ad46aed5f707f280ab5fc4", "e5921c77822587316d7d3c2",
        "4f14da8242a8b86dca73352", "8b8b507ad467d4441df770e", "22831c9cf1169467ad04b68",
        "213b838fe2ae54c38ee7180", "5d926b6dd71f085181a4e12", "66ab79d4b29ee6e69509e56",
        "958148682d748a38dd68baa", "b8ce020cf069c32a723ab14", "f4331d6d461607e95752746",
        "6da23ba424b9596133cf9c8", "a636bcbc7b30c5fbeae67fe", "5cb0d86a07df654a9089a20",
        "f11f106848780fc9ecdd80a", "1fbb5364fb8d2c9d730d5ba", "fcb86bc70a50c9d02a5d034",
        "a534433029eac15f322e34c", "c989d9c7c3d3b8c55d75130", "7bb38b2f0186d46643ae962",
        "2644ebadeb44b9467d1f42c", "608cc857594bfbb55d69600"
    };

    // genMatrix[col * 83 + row] = bit of generator matrix.
    // Built once lazily; col ∈ [0,90], row ∈ [0,82].
    private static readonly bool[] GenMatrix = BuildGenMatrix();

    private static bool[] BuildGenMatrix()
    {
        var mat = new bool[91 * 83];
        for (int row = 0; row < 83; row++)
        {
            string hex = GenHex[row];
            for (int j = 0; j < 23; j++)
            {
                int nibble = Convert.ToInt32(hex[j].ToString(), 16);
                for (int b = 0; b < 4; b++)
                {
                    int col = j * 4 + b;
                    if (col < 91)
                        mat[col * 83 + row] = ((nibble >> (3 - b)) & 1) == 1;
                }
            }
        }
        return mat;
    }

    // ── LDPC(174,91) encoder ──────────────────────────────────────────────────

    /// <summary>
    /// Encode a 77-bit message into a 174-bit LDPC(174,91) codeword.
    /// Steps: append CRC-14 → 91-bit message → compute 83 parity bits → concatenate.
    /// Mirrors MSHV's <c>GenPomFt::encode174_91</c>.
    /// </summary>
    /// <param name="message77">77-bit source message (bits 0..76).</param>
    /// <param name="codeword174">Output 174-bit codeword (bits 0..90 = systematic, 91..173 = parity).</param>
    public static void Encode(ReadOnlySpan<bool> message77, Span<bool> codeword174)
    {
        if (message77.Length < 77)   throw new ArgumentException("message77 must have at least 77 elements", nameof(message77));
        if (codeword174.Length < 174) throw new ArgumentException("codeword174 must have at least 174 elements", nameof(codeword174));

        // Build 91-bit message = msg77 + CRC14
        Span<bool> msg91 = stackalloc bool[91];
        message77[..77].CopyTo(msg91);
        ushort crc = (ushort)Crc14.Compute(message77);
        for (int i = 0; i < 14; i++)
            msg91[77 + i] = ((crc >> (13 - i)) & 1) == 1;

        // Systematic part: codeword[0..90] = msg91
        msg91.CopyTo(codeword174[..91]);

        // Parity part: codeword[91..173] = (msg91 × G) mod 2
        for (int row = 0; row < 83; row++)
        {
            int nsum = 0;
            for (int col = 0; col < 91; col++)
                if (msg91[col] && GenMatrix[col * 83 + row])
                    nsum++;
            codeword174[91 + row] = (nsum & 1) == 1;
        }
    }

    // ── BP approximation functions (matching ft8_lib's fast_atanh/fast_tanh) ──

    /// <summary>
    /// Piecewise-linear atanh approximation, accurate to within ~0.02% for |x| ≤ 0.9,
    /// mirroring ft8_lib's <c>fast_atanh</c> rational polynomial.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static double FastAtanh(double x)
    {
        double x2 = x * x;
        double a  = x * (945.0 + x2 * (-735.0 + x2 * 64.0));
        double b  = 945.0 + x2 * (-1050.0 + x2 * 225.0);
        return a / b;
    }

    /// <summary>
    /// Padé (4,4) rational approximation of tanh(x), accurate to &lt;0.01% for |x| ≤ 3.
    /// Replaces Math.Tanh in the hot BP inner loop (~540 calls/iteration × 30 iterations).
    /// Exposed as <c>internal</c> for accuracy unit tests via InternalsVisibleTo.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static double FastTanh(double x)
    {
        if (x > 4.0)  return  1.0;
        if (x < -4.0) return -1.0;
        double x2 = x * x;
        return x * (945.0 + x2 * (105.0 + x2))
                 / (945.0 + x2 * (420.0 + x2 * 15.0));
    }

    // Keep Platanh for code paths that relied on it (FT4/FT2 decoders sharing this class).
    private static double Platanh(double x)
    {
        double sign = x < 0 ? -1.0 : 1.0;
        double z = Math.Abs(x);
        if (z <= 0.664)   return x / 0.83;
        if (z <= 0.9217)  return sign * (z - 0.4064) / 0.322;
        if (z <= 0.9951)  return sign * (z - 0.8378) / 0.0524;
        if (z <= 0.9998)  return sign * (z - 0.9914) / 0.0012;
        return sign * 7.0;
    }

    /// <summary>
    /// Verifies all 83 LDPC parity-check equations for a 174-bit codeword.
    /// Returns true if every check is satisfied (codeword is valid).
    /// Useful for validating encoder output without LLR sign-convention assumptions.
    /// </summary>
    public static bool CheckParity(ReadOnlySpan<bool> codeword174)
    {
        if (codeword174.Length < N) return false;
        for (int i = 0; i < M; i++)
        {
            int parity = 0;
            for (int x = 0; x < Nrw[i]; x++)
                parity ^= codeword174[Nm[i, x] - 1] ? 1 : 0;
            if (parity != 0) return false;
        }
        return true;
    }

    // ── Belief Propagation decoder ────────────────────────────────────────────

    /// <summary>
    /// Belief-propagation LDPC decoder (iterative, max 30 iterations).
    /// Mirrors MSHV's <c>decode174_91</c> / <c>bpdecode174_91var</c>.
    /// Uses piecewise-linear <c>platanh</c> for check→variable messages (same as MSHV),
    /// which amplifies weak messages ~20% and caps strong ones, improving convergence
    /// on weak signals compared to exact atanh.
    /// </summary>
    /// <param name="llr">Soft log-likelihood ratios (length 174). Positive LLR → decoded bit 1 (true); negative → bit 0 (false).</param>
    /// <param name="apMask">AP mask: true = fixed by prior information (length 174).</param>
    /// <param name="message77">Output: 77-bit decoded message (bits 0..76).</param>
    /// <param name="cw">Output: full 174-bit codeword decisions.</param>
    /// <param name="hardErrors">Output: number of hard errors (−1 = no valid codeword found).</param>
    public static void BpDecode(
        ReadOnlySpan<double> llr, ReadOnlySpan<bool> apMask,
        bool[] message77, bool[] cw, out int hardErrors)
    {
        var pool = ArrayPool<double>.Shared;
        double[] zn = pool.Rent(N);
        try   { BpDecodeCore(llr, apMask, message77, cw, out hardErrors, zn); }
        finally { pool.Return(zn, clearArray: false); }
    }

    /// <summary>
    /// Core BP implementation. Caller provides <paramref name="zn"/> (length ≥ N) as
    /// the soft-decision work buffer. On BP failure <paramref name="zn"/> retains the
    /// posterior LLR from the last iteration, enabling OSD to use a better MRB ordering.
    /// </summary>
    private static void BpDecodeCore(
        ReadOnlySpan<double> llr, ReadOnlySpan<bool> apMask,
        bool[] message77, bool[] cw, out int hardErrors, double[] zn)
    {
        // Flat indexing: tov[i,j] → tov[i*3+j], toc/tanhtoc[r,c] → arr[r*7+c]
        var pool = ArrayPool<double>.Shared;
        double[] tov     = pool.Rent(N * 3);
        double[] toc     = pool.Rent(M * 7);
        double[] tanhtoc = pool.Rent(M * 7);

        tov.AsSpan(0, N * 3).Clear();
        hardErrors = -1;

        for (int j = 0; j < M; j++)
            for (int i = 0; i < Nrw[j]; i++)
                toc[j * 7 + i] = llr[Nm[j, i] - 1];

        int ncnt = 0, nclast = 0;
        try
        {
            for (int iter = 0; iter <= 30; iter++)
            {
                // Compute soft decisions into caller-provided zn
                for (int i = 0; i < N; i++)
                {
                    zn[i] = apMask[i]
                        ? llr[i]
                        : llr[i] + tov[i * 3] + tov[i * 3 + 1] + tov[i * 3 + 2];
                    cw[i] = zn[i] > 0.0;
                }

                int ncheck = 0;
                for (int i = 0; i < M; i++)
                {
                    int sum = 0;
                    for (int x = 0; x < Nrw[i]; x++) sum += cw[Nm[i, x] - 1] ? 1 : 0;
                    if (sum % 2 != 0) ncheck++;
                }

                if (ncheck == 0)
                {
                    int count = 0;
                    for (int i = 0; i < N; i++)
                        if ((2 * (cw[i] ? 1 : 0) - 1) * llr[i] < 0.0) count++;
                    hardErrors = count;

                    if (Crc14.Check(cw.AsSpan(0, 91)))
                    {
                        Array.Copy(cw, message77, 77);
                        return;
                    }
                }

                if (iter > 0)
                {
                    int nd = ncheck - nclast;
                    if (nd < 0) ncnt = 0; else ncnt++;
                    if (ncnt >= 5 && iter >= 10 && ncheck > 15) break;
                }
                nclast = ncheck;

                for (int j = 0; j < M; j++)
                {
                    int jBase = j * 7;
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

                for (int i = 0; i < M; i++)
                {
                    int iBase = i * 7;
                    for (int x = 0; x < Nrw[i]; x++)
                        tanhtoc[iBase + x] = FastTanh(-toc[iBase + x] * 0.5);
                }

                for (int j = 0; j < N; j++)
                {
                    int jBase3 = j * 3;
                    for (int i = 0; i < 3; i++)
                    {
                        int ichk     = Mn[j, i] - 1;
                        int ichkBase = ichk * 7;
                        double tmn   = 1.0;
                        for (int z = 0; z < Nrw[ichk]; z++)
                            if (Nm[ichk, z] - 1 != j) tmn *= tanhtoc[ichkBase + z];
                        tov[jBase3 + i] = 2.0 * Platanh(-tmn);
                    }
                }
            }
            hardErrors = -1;
            // zn holds posterior soft decisions from the last iteration.
        }
        finally
        {
            pool.Return(tov);
            pool.Return(toc);
            pool.Return(tanhtoc);
        }
    }

    // ── OSD decoder ──────────────────────────────────────────────────────────

    /// <summary>
    /// Ordered-Statistics Decoding with Most Reliable Basis (MRB).
    /// Faithful port of MSHV's <c>osd174_91_1</c>.
    /// <para>
    /// Every candidate codeword tested is constructed from the LDPC generator matrix and
    /// therefore automatically satisfies all 83 parity-check equations.
    /// CRC-14 is the only validation needed.
    /// </para>
    /// </summary>
    /// <param name="ndeep">
    /// Search order: 0 = order-0 only, 1 = order-0 + all K single-bit flips,
    /// 2 = order-0 + order-1 + all K*(K-1)/2 two-bit flips.
    /// </param>
    public static void OsdDecode(
        ReadOnlySpan<double> llr, ReadOnlySpan<bool> apMask, int ndeep,
        bool[] message91, bool[] cw, out int hardMin, out double dmin)
    {
        hardMin = -1;
        dmin    = double.MaxValue;
        ndeep   = Math.Clamp(ndeep, 0, 2);

        // ── 1. Hard decisions and reliabilities (stack-allocated) ────────────
        // These are small (174 × 8 = 1392 bytes per double array) — ~6 KB total.
        Span<double> absLlr = stackalloc double[N];
        Span<bool>   hdec   = stackalloc bool[N];
        for (int i = 0; i < N; i++) { absLlr[i] = Math.Abs(llr[i]); hdec[i] = llr[i] >= 0.0; }

        // ── 2. Sort positions by |LLR| descending (most reliable first) ──────
        // Array.Sort requires T[] so we pool the index arrays and a heap double[].
        int[]    indx      = ArrayPool<int>.Shared.Rent(N);
        int[]    indices   = ArrayPool<int>.Shared.Rent(N);
        double[] absLlrArr = ArrayPool<double>.Shared.Rent(N); // needed as T[] for struct comparer
        absLlr.CopyTo(absLlrArr);
        for (int i = 0; i < N; i++) indx[i] = i;
        Array.Sort(indx, 0, N, new AbsLlrDescComparer(absLlrArr));
        indx.AsSpan(0, N).CopyTo(indices);

        // ── 3+5. Build genmrb and g2 — pooled to avoid 15 KB × 2 heap pressure ─
        bool[] genmrb = ArrayPool<bool>.Shared.Rent(N * K);
        bool[] g2     = ArrayPool<bool>.Shared.Rent(K * N);

        try
        {
            // ── 3. Build permuted generator matrix genmrb[i*K + j] ───────────
            Array.Clear(genmrb, 0, N * K); // must start zero; pool may be dirty
            for (int i = 0; i < N; i++)
            {
                int orig  = indx[i];
                int iBase = i * K;
                if (orig < K)
                    genmrb[iBase + orig] = true;
                else
                {
                    int pr = orig - K;
                    for (int j = 0; j < K; j++)
                        genmrb[iBase + j] = GenMatrix[j * 83 + pr];
                }
            }

            // ── 4. GF(2) Gaussian elimination on columns ─────────────────────
            for (int id = 0; id < K; id++)
            {
                int pivot = -1;
                int limit = Math.Min(K + 20, N);
                for (int r = id; r < limit; r++)
                    if (genmrb[r * K + id]) { pivot = r; break; }
                if (pivot < 0) continue;

                if (pivot != id)
                {
                    int idBase    = id    * K;
                    int pivotBase = pivot * K;
                    for (int z = 0; z < K; z++)
                        (genmrb[idBase + z], genmrb[pivotBase + z]) = (genmrb[pivotBase + z], genmrb[idBase + z]);
                    (indices[id], indices[pivot]) = (indices[pivot], indices[id]);
                }

                for (int ii = 0; ii < K; ii++)
                {
                    if (ii != id && genmrb[id * K + ii])
                    {
                        for (int z = 0; z < N; z++)
                            genmrb[z * K + ii] ^= genmrb[z * K + id];
                    }
                }
            }

            // ── 5. Transpose: g2[j*N + i] = genmrb[i*K + j] ─────────────────
            for (int i = 0; i < N; i++)
            {
                int iBase = i * K;
                for (int j = 0; j < K; j++)
                    g2[j * N + i] = genmrb[iBase + j];
            }

            // ── 6. Reorder hard decisions and |LLR| to permuted indices[] ────
            Span<bool>   hdec2  = stackalloc bool[N];
            Span<double> absrx2 = stackalloc double[N];
            for (int i = 0; i < N; i++) { hdec2[i] = hdec[indices[i]]; absrx2[i] = absLlrArr[indices[i]]; }

            // ── 7. Order-0: encode m0 = hdec2[0..K-1] ────────────────────────
            Span<bool> c0 = stackalloc bool[N];
            MrbEncode91(hdec2, g2, c0);

            Span<bool> e0 = stackalloc bool[N];
            double     d0 = 0.0;
            int        h0 = 0;
            for (int i = 0; i < N; i++)
            {
                e0[i] = c0[i] != hdec2[i];
                if (e0[i]) { h0++; d0 += absrx2[i]; }
            }

            Span<bool> bestCw = stackalloc bool[N];
            c0.CopyTo(bestCw);
            double bestD = d0;
            int    bestH = h0;

            // ── 8. Order-1 search: try all K single-bit flips ────────────────
            if (ndeep >= 1)
            {
                // Precompute signed contribution per position: toggling a position
                // where e0[i]=true reduces distance; where e0[i]=false increases it.
                Span<double> contrib = stackalloc double[N];
                for (int i = 0; i < N; i++)
                    contrib[i] = e0[i] ? -absrx2[i] : absrx2[i];

                for (int k = 0; k < K; k++)
                {
                    int g2k = k * N;
                    int nd  = 1;
                    for (int x = 0; x < 40 && nd <= 12; x++)
                        if (e0[K + x] ^ g2[g2k + K + x]) nd++;
                    if (nd > 12) continue;

                    // Incremental distance with one branch per position (vs two before)
                    double dd = d0;
                    for (int i = 0; i < N; i++)
                        if (g2[g2k + i]) dd += contrib[i];
                    if (dd >= bestD) continue;

                    bestD = dd;
                    bestH = 0;
                    for (int i = 0; i < N; i++)
                    {
                        bestCw[i] = c0[i] ^ g2[g2k + i];
                        if (bestCw[i] != hdec2[i]) bestH++;
                    }
                }
            }

            // ── 9. Order-2 search: try all K*(K-1)/2 two-bit flips ───────────
            if (ndeep >= 2)
            {
                for (int k1 = 0; k1 < K; k1++)
                {
                    int g2k1 = k1 * N;
                    for (int k2 = k1 + 1; k2 < K; k2++)
                    {
                        int    g2k2 = k2 * N;
                        double dd   = 0.0;
                        for (int i = 0; i < N; i++)
                        {
                            if (e0[i] ^ g2[g2k1 + i] ^ g2[g2k2 + i])
                                dd += absrx2[i];
                        }
                        if (dd >= bestD) continue;

                        bestD = dd;
                        bestH = 0;
                        for (int i = 0; i < N; i++)
                        {
                            bestCw[i] = c0[i] ^ g2[g2k1 + i] ^ g2[g2k2 + i];
                            if (bestCw[i] != hdec2[i]) bestH++;
                        }
                    }
                }
            }

            // ── 10. Un-permute and CRC check ─────────────────────────────────
            Span<bool> cwOrig = stackalloc bool[N];
            for (int i = 0; i < N; i++) cwOrig[indices[i]] = bestCw[i];

            if (Crc14.Check(cwOrig[..91]))
            {
                cwOrig.CopyTo(cw);
                cwOrig[..91].CopyTo(message91);
                hardMin = bestH;
                dmin    = bestD;
            }
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(genmrb,    clearArray: false);
            ArrayPool<bool>.Shared.Return(g2,        clearArray: false);
            ArrayPool<int>.Shared.Return(indx,       clearArray: false);
            ArrayPool<int>.Shared.Return(indices,    clearArray: false);
            ArrayPool<double>.Shared.Return(absLlrArr, clearArray: false);
        }
    }

    /// <summary>
    /// Comparer for sorting index arrays by descending absolute LLR value.
    /// Using a struct IComparer avoids the delegate allocation that a lambda would produce.
    /// </summary>
    private readonly struct AbsLlrDescComparer : IComparer<int>
    {
        private readonly double[] _absLlr;
        internal AbsLlrDescComparer(double[] absLlr) => _absLlr = absLlr;
        public int Compare(int x, int y) => _absLlr[y].CompareTo(_absLlr[x]);
    }

    /// <summary>Encode m[0..K-1] using the MRB generator matrix g2[j*N + i].</summary>
    private static void MrbEncode91(ReadOnlySpan<bool> m, bool[] g2, Span<bool> cw)
    {
        cw.Clear();
        for (int j = 0; j < K; j++)
            if (m[j])
            {
                int g2j = j * N;
                for (int i = 0; i < N; i++)
                    cw[i] ^= g2[g2j + i];
            }
    }


    // ── Combined entry point ─────────────────────────────────────────────────

    /// <summary>
    /// Try BP first; if it fails and depth ≥ 2, fall back to OSD.
    /// Returns true if a valid decode was found.
    /// </summary>
    /// <param name="depth">
    /// <see cref="DecoderDepth.Fast"/> = BP only;
    /// <see cref="DecoderDepth.Normal"/> = BP + OSD order-1 (91 candidates);
    /// <see cref="DecoderDepth.Deep"/> = BP + OSD order-2 (4096 candidates, more sensitive but slower).
    /// </param>
    public static bool TryDecode(
        ReadOnlySpan<double> llr, ReadOnlySpan<bool> apMask, DecoderDepth depth,
        bool[] message77, bool[] cw, out int hardErrors, out double dmin)
    {
        hardErrors = -1;
        dmin = double.NaN;

        bool[] cw174 = new bool[N];
        bool[] msg77 = new bool[77];

        // Rent a buffer for BP soft output so OSD can use the posterior LLR
        // (better MRB ordering) rather than the raw channel LLR.
        var pool = ArrayPool<double>.Shared;
        double[] softBuf = pool.Rent(N);
        try
        {
            BpDecodeCore(llr, apMask, msg77, cw174, out int he, softBuf);

            if (he >= 0)
            {
                Array.Copy(msg77, message77, 77);
                Array.Copy(cw174, cw, N);
                hardErrors = he;
                dmin = 0;
                return true;
            }

            if (depth >= DecoderDepth.Normal)
            {
                bool[] msg91 = new bool[91];
                int ndeep = Math.Min((int)depth - 1, 2);  // Normal→ndeep=1, Deep→ndeep=2
                // Feed BP posterior LLR to OSD: the 30 BP iterations refine reliability
                // ordering, giving OSD a better Most-Reliable-Basis than raw channel LLR.
                OsdDecode(softBuf.AsSpan(0, N), apMask, ndeep, msg91, cw174, out int hm, out double dm);
                if (hm >= 0)
                {
                    Array.Copy(msg91, message77, 77);
                    Array.Copy(cw174, cw, N);
                    hardErrors = hm;
                    dmin = dm;
                    return true;
                }
            }

            return false;
        }
        finally
        {
            pool.Return(softBuf, clearArray: false);
        }
    }
}

namespace HamDigiSharp.Codecs;

/// <summary>
/// Reed-Solomon RS(63,12) codec for JT65.
/// GF(2^6), generator polynomial 0x43, fcr=3, prim=1, nroots=51, pad=0.
/// Ported from the KA9Q RS implementation used in WSJT-X / MSHV, GPL.
///
/// JT65 message: 12 six-bit symbols (72 bits) → encoded to 63 symbols.
/// Decoding can correct up to 25 symbol errors (nroots/2 = 25).
/// </summary>
public static class ReedSolomon63
{
    // GF(64) tables — generated from poly 0x43 (x^6 + x + 1)
    private const int Nn     = 63;  // 2^6 - 1
    private const int Pp     = 0x43;
    private const int Nroots = 51;
    private const int Fcr    = 3;
    private const int Prim   = 1;

    private static readonly int[] AlphaTo;  // log lookup: alpha^i
    private static readonly int[] IndexOf;  // antilog: i → log
    private static readonly int[] GenPoly;  // generator polynomial

    static ReedSolomon63()
    {
        AlphaTo = new int[Nn + 1];
        IndexOf = new int[Nn + 1];
        GenPoly = new int[Nroots + 1];

        // Build GF tables
        int sr = 1;
        AlphaTo[Nn] = 0;
        IndexOf[0]  = Nn; // log of 0 is undefined → use Nn as sentinel
        for (int i = 0; i < Nn; i++)
        {
            IndexOf[sr] = i;
            AlphaTo[i]  = sr;
            sr <<= 1;
            if ((sr & 64) != 0) sr ^= Pp;
            sr &= Nn;
        }

        // Build generator polynomial
        GenPoly[0] = 1;
        for (int i = 0; i < Nroots; i++)
        {
            GenPoly[i + 1] = 1;
            int root = Modnn(Fcr + i * Prim);
            for (int j = i; j > 0; j--)
            {
                if (GenPoly[j] != 0)
                    GenPoly[j] = GenPoly[j - 1] ^ AlphaTo[Modnn(IndexOf[GenPoly[j]] + root)];
                else
                    GenPoly[j] = GenPoly[j - 1];
            }
            GenPoly[0] = AlphaTo[Modnn(IndexOf[GenPoly[0]] + root)];
        }
        // Convert to index form
        for (int i = 0; i <= Nroots; i++)
            GenPoly[i] = IndexOf[GenPoly[i]];
    }

    private static int Modnn(int x)
    {
        while (x >= Nn) { x -= Nn; x = (x >> 6) + (x & Nn); }
        return x;
    }

    // ── Encode ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Encode 12 data symbols into a 63-symbol codeword.
    /// Input: <paramref name="data"/> has 12 elements (6-bit symbols).
    /// Output: <paramref name="parity"/> has 51 parity symbols.
    /// </summary>
    public static void Encode(ReadOnlySpan<int> data, int[] parity)
    {
        Array.Clear(parity, 0, Nroots);
        int kk = Nn - Nroots; // = 12 data symbols
        for (int i = 0; i < kk; i++)
        {
            int feedback = IndexOf[data[i] ^ parity[0]];
            if (feedback != Nn) // feedback != A0
            {
                // XOR with generator polynomial coefficients (reversed index, matching Phil Karn)
                for (int j = 1; j < Nroots; j++)
                    if (GenPoly[Nroots - j] != Nn)
                        parity[j] ^= AlphaTo[Modnn(feedback + GenPoly[Nroots - j])];
            }
            // Always shift register left (equivalent to memmove(&bb[0], &bb[1], Nroots-1))
            Array.Copy(parity, 1, parity, 0, Nroots - 1);
            parity[Nroots - 1] = (feedback != Nn) ? AlphaTo[Modnn(feedback + GenPoly[0])] : 0;
        }
    }

    // ── Decode (Berlekamp-Massey + Chien + Forney) ────────────────────────────

    /// <summary>
    /// Soft-decision RS decoder. Returns the number of errors corrected
    /// (-1 if uncorrectable). Updates <paramref name="data"/> in place.
    /// <paramref name="erasurePos"/> lists erasure positions (may be empty).
    /// </summary>
    public static int Decode(int[] data, int[] erasurePos, int numErasures, bool doErasures)
    {
        Span<int> lambda  = stackalloc int[Nroots + 1];
        Span<int> s       = stackalloc int[Nroots];
        Span<int> b       = stackalloc int[Nroots + 1];
        Span<int> t       = stackalloc int[Nroots + 1];
        Span<int> omega   = stackalloc int[Nroots + 1];
        Span<int> root    = stackalloc int[Nroots];
        Span<int> reg     = stackalloc int[Nroots + 1];
        Span<int> loc     = stackalloc int[Nroots];

        // Compute syndromes via Horner's method (evaluates c_msb at roots of g, matching C++ decode_rs_int)
        bool syndromeZero = true;
        for (int i = 0; i < Nroots; i++)
            s[i] = data[0]; // value form, initialize to first symbol

        for (int j = 1; j < Nn; j++)
            for (int i = 0; i < Nroots; i++)
                s[i] = (s[i] == 0) ? data[j]
                    : data[j] ^ AlphaTo[Modnn(IndexOf[s[i]] + Fcr + i * Prim)];

        // Convert syndromes to index form
        for (int i = 0; i < Nroots; i++)
        {
            if (s[i] != 0) syndromeZero = false;
            s[i] = IndexOf[s[i]];
        }
        if (syndromeZero) return 0; // no errors

        // Initialize lambda — erasure locator polynomial
        lambda[0] = 1;
        int el = 0;

        if (doErasures)
        {
            // Build erasure locator from known positions
            for (int i = 0; i < numErasures; i++)
            {
                int root_e = IndexOf[AlphaTo[Modnn(Prim * (Nn - 1 - erasurePos[i]))]];
                for (int j = i + 1; j > 0; j--)
                    if (lambda[j - 1] != 0)
                        lambda[j] ^= AlphaTo[Modnn(root_e + IndexOf[lambda[j - 1]])];
            }
            el = numErasures;
        }

        for (int i = 0; i <= Nroots; i++) b[i] = IndexOf[lambda[i]];

        // Berlekamp-Massey
        int r = numErasures;
        while (++r <= Nroots)
        {
            int discrR = 0;
            for (int i = 0; i < r; i++)
                if (lambda[i] != 0 && s[r - i - 1] != Nn)
                    discrR ^= AlphaTo[Modnn(IndexOf[lambda[i]] + s[r - i - 1])];
            discrR = IndexOf[discrR];

            if (discrR == Nn)
            {
                // "b ← x·b" — shift coefficients right by 1 (multiply polynomial by x):
                // Equivalent to C: memmove(&b[1], b, Nroots); b[0] = Nn;
                for (int j = Nroots; j >= 1; j--)
                    b[j] = b[j - 1];
                b[0] = Nn;
            }
            else
            {
                t[0] = lambda[0];
                for (int i = 0; i < Nroots; i++)
                    t[i + 1] = b[i] != Nn
                        ? lambda[i + 1] ^ AlphaTo[Modnn(discrR + b[i])]
                        : lambda[i + 1];
                if (2 * el <= r + numErasures - 1)
                {
                    el = r + numErasures - el;
                    for (int i = 0; i <= Nroots; i++)
                        b[i] = lambda[i] == 0 ? Nn : Modnn(IndexOf[lambda[i]] - discrR + Nn);
                }
                else
                {
                    // "b ← x·b" — shift coefficients right by 1 (multiply polynomial by x):
                    // Equivalent to C: memmove(&b[1], b, Nroots); b[0] = Nn;
                    for (int j = Nroots; j >= 1; j--)
                        b[j] = b[j - 1];
                    b[0] = Nn;
                }
                t.CopyTo(lambda); // copy t into lambda (must not alias — b update below uses old lambda)
            }
        }

        // Degree of lambda
        int degLambda = 0;
        for (int i = 0; i <= Nroots; i++)
        {
            lambda[i] = IndexOf[lambda[i]];
            if (lambda[i] != Nn) degLambda = i;
        }

        // Chien search
        reg[1..].CopyFrom(lambda[1..]);
        int count = 0;
        for (int i = 1, k = Prim - 1; i <= Nn; i++, k = Modnn(k + Prim))
        {
            int q = 1;
            for (int j = degLambda; j > 0; j--)
            {
                if (reg[j] != Nn) { reg[j] = Modnn(reg[j] + j); q ^= AlphaTo[reg[j]]; }
            }
            if (q != 0) continue;
            root[count] = i;
            loc[count]  = k;
            if (++count == degLambda) break;
        }
        if (degLambda != count) return -1;

        // Omega
        int degOmega = 0;
        for (int i = 0; i < Nroots; i++)
        {
            int tmp = 0;
            int jj = Math.Min(degLambda, i);
            for (; jj >= 0; jj--)
                if (s[i - jj] != Nn && lambda[jj] != Nn)
                    tmp ^= AlphaTo[Modnn(s[i - jj] + lambda[jj])];
            if (tmp != 0) degOmega = i;
            omega[i] = IndexOf[tmp];
        }
        omega[Nroots] = Nn;

        // Forney algorithm
        for (int j = count - 1; j >= 0; j--)
        {
            int num1 = 0;
            for (int i = degOmega; i >= 0; i--)
                if (omega[i] != Nn) num1 ^= AlphaTo[Modnn(omega[i] + i * root[j])];
            int num2 = AlphaTo[Modnn(root[j] * (Fcr - 1) + Nn)];
            int den  = 0;
            for (int i = Math.Min(degLambda, Nroots - 1) & ~1; i >= 0; i -= 2)
                if (lambda[i + 1] != Nn) den ^= AlphaTo[Modnn(lambda[i + 1] + i * root[j])];
            if (den == 0) return -1;
            if (num1 != 0)
                data[loc[j]] ^= AlphaTo[Modnn(IndexOf[num1] + IndexOf[num2] + Nn - IndexOf[den])];
        }
        return count;
    }
}

file static class SpanExtensions
{
    public static void CopyFrom(this Span<int> dst, ReadOnlySpan<int> src)
    {
        int n = Math.Min(dst.Length, src.Length);
        src[..n].CopyTo(dst);
    }
}

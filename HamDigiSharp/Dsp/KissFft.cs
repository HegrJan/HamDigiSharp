// C# port of KISS FFT (https://github.com/mborgerding/kissfft).
//
// Copyright (c) 2003-2010, Mark Borgerding. All rights reserved.
// This file is part of KISS FFT - https://github.com/mborgerding/kissfft
// SPDX-License-Identifier: BSD-3-Clause
//
// Redistribution and use in source and binary forms, with or without modification,
// are permitted provided that the following conditions are met:
//   * Redistributions of source code must retain the above copyright notice, this
//     list of conditions and the following disclaimer.
//   * Redistributions in binary form must reproduce the above copyright notice, this
//     list of conditions and the following disclaimer in the documentation and/or
//     other materials provided with the distribution.
//   * Neither the author nor the names of any contributors may be used to endorse or
//     promote products derived from this software without specific prior written
//     permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY
// EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES
// OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT
// SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
// INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED
// TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR
// BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY
// WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace HamDigiSharp.Dsp;

/// <summary>
/// Immutable, thread-safe KISS FFT plan for one length and direction
/// (the C# counterpart of <c>kiss_fft_alloc(nfft, inverse_fft, …)</c>).
/// <para>
/// The algorithm is Borgerding's: factor N into radices 4, 2, 3, 5 and odd primes
/// (<c>kf_factor</c>), recurse through the factors (<c>kf_work</c>) and combine the
/// sub-transforms with radix-specific butterflies (<c>kf_bfly2/3/4/5/generic</c>).
/// Like the original, no scaling is applied in either direction.
/// </para>
/// <para>
/// The one structural change is the twiddle storage. KISS reads <c>tw[k·fstride·q]</c>,
/// which strides through the table; here each stage gets contiguous per-radix tables
/// laid out like the interleaved (re, im) sample data so the butterflies can load
/// twiddles and samples with the same SIMD offsets:
/// <c>C = [cos, cos]</c>, <c>S = [−sin, +sin]</c>, giving
/// <c>x·tw = x·C + swap(x)·S</c> (swap exchanges re/im within each complex pair).
/// Every SIMD kernel performs the same IEEE operations in the same order as its scalar
/// fallback, so results are bit-identical regardless of the instruction set.
/// </para>
/// <para>
/// Prime lengths degrade to the O(N²) generic butterfly, exactly as in KISS FFT.
/// </para>
/// </summary>
internal sealed class KissFftPlan
{
    /// <summary>One level of the <c>kf_work</c> recursion.</summary>
    private sealed class Stage(int p, int m, int fstride, double[] twC, double[] twS)
    {
        public readonly int P = p;             // radix
        public readonly int M = m;             // sub-transform length (N / (fstride·p))
        public readonly int FStride = fstride; // twiddle stride at this level
        // Per-radix twiddle tables: multiplier q (1..P-1) occupies [(q-1)·2M, q·2M).
        public readonly double[] TwC = twC;
        public readonly double[] TwS = twS;
    }

    private readonly Stage[] _stages;
    private readonly double[] _tw; // raw twiddles, interleaved (re, im), length 2N

    public int Length { get; }
    public bool Inverse { get; }

    static KissFftPlan()
    {
        // The butterflies view Complex spans as interleaved doubles.
        Complex probe = new(1.0, 2.0);
        var view = MemoryMarshal.Cast<Complex, double>(MemoryMarshal.CreateReadOnlySpan(ref probe, 1));
        if (Unsafe.SizeOf<Complex>() != 2 * sizeof(double) || view[0] != 1.0 || view[1] != 2.0)
            throw new PlatformNotSupportedException("System.Numerics.Complex is not laid out as (Real, Imaginary) doubles.");
    }

    public KissFftPlan(int n, bool inverse)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n);
        Length  = n;
        Inverse = inverse;

        _tw = new double[2 * n];
        for (int i = 0; i < n; i++)
        {
            double phase = (inverse ? 2.0 : -2.0) * Math.PI * i / n;
            _tw[2 * i]     = Math.Cos(phase);
            _tw[2 * i + 1] = Math.Sin(phase);
        }

        int[] factors = Factor(n);
        _stages = new Stage[factors.Length / 2];
        int fstride = 1;
        for (int d = 0; d < _stages.Length; d++)
        {
            int p = factors[2 * d], m = factors[2 * d + 1];
            double[] twC = [], twS = [];
            if (p <= 5)
            {
                twC = new double[2 * m * (p - 1)];
                twS = new double[2 * m * (p - 1)];
                for (int q = 1; q < p; q++)
                {
                    int baseIdx = 2 * m * (q - 1);
                    for (int k = 0; k < m; k++)
                    {
                        int t = q * fstride * k;
                        double re = _tw[2 * t], im = _tw[2 * t + 1];
                        twC[baseIdx + 2 * k] = re;
                        twC[baseIdx + 2 * k + 1] = re;
                        twS[baseIdx + 2 * k] = -im;
                        twS[baseIdx + 2 * k + 1] = im;
                    }
                }
            }
            _stages[d] = new Stage(p, m, fstride, twC, twS);
            fstride *= p;
        }
    }

    /// <summary>
    /// Port of <c>kf_factor</c>: radix 4 first, then 2, then 3, 5, 7, …; once the trial
    /// radix exceeds ⌊√N⌋ the remainder is taken as the last factor.
    /// Returns (p, m) pairs where m is the length remaining after dividing by p.
    /// </summary>
    internal static int[] Factor(int n)
    {
        var fac = new List<int>();
        int p = 4;
        double floorSqrt = Math.Floor(Math.Sqrt(n));
        do
        {
            while (n % p != 0)
            {
                p = p switch { 4 => 2, 2 => 3, _ => p + 2 };
                if (p > floorSqrt) p = n;
            }
            n /= p;
            fac.Add(p);
            fac.Add(n);
        } while (n > 1);
        return [.. fac];
    }

    /// <summary>
    /// Port of <c>kiss_fft_stride</c>. <paramref name="input"/> is read every
    /// <paramref name="inStride"/> elements. KISS handles in-place calls by transforming
    /// into a temporary buffer and copying back; here the (strided) input is copied to a
    /// dense temporary instead — stack-allocated for small N, pooled otherwise — and the
    /// transform writes straight into <paramref name="output"/>.
    /// </summary>
    public void Transform(ReadOnlySpan<Complex> input, Span<Complex> output, int inStride = 1)
    {
        int n = Length;
        ArgumentOutOfRangeException.ThrowIfLessThan(inStride, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(output.Length, n);
        ArgumentOutOfRangeException.ThrowIfLessThan(input.Length, (n - 1) * inStride + 1);
        output = output[..n];
        var fout = MemoryMarshal.Cast<Complex, double>(output);

        if (!input.Overlaps(output))
        {
            Work(fout, MemoryMarshal.Cast<Complex, double>(input), 0, inStride, 0);
            return;
        }

        const int StackLimit = 512;
        Complex[]? rented = null;
        Span<Complex> tmp = n <= StackLimit
            ? stackalloc Complex[n]
            : (rented = ArrayPool<Complex>.Shared.Rent(n)).AsSpan(0, n);

        if (inStride == 1)
            input[..n].CopyTo(tmp);
        else
            for (int i = 0; i < n; i++) tmp[i] = input[i * inStride];

        Work(fout, MemoryMarshal.Cast<Complex, double>(tmp), 0, 1, 0);

        if (rented is not null) ArrayPool<Complex>.Shared.Return(rented);
    }

    /// <summary>Port of <c>kf_work</c>. <paramref name="inOff"/> is a complex index into <paramref name="fin"/>.</summary>
    private void Work(Span<double> fout, ReadOnlySpan<double> fin, int inOff, int inStride, int depth)
    {
        Stage st = _stages[depth];
        int p = st.P, m = st.M;
        int step = st.FStride * inStride;

        if (m == 1)
        {
            // At the leaves the butterfly only ever uses tw[0] = 1, so for the hot radices
            // fold kf_work's copy and kf_bfly2/kf_bfly4 into one step (N=32 has 16 leaves).
            if (p == 2) { Leaf2(fout, fin, 2 * inOff, 2 * step); return; }
            if (p == 4) { Leaf4(fout, fin, 2 * inOff, 2 * step, Inverse); return; }

            for (int i = 0; i < p; i++, inOff += step)
            {
                fout[2 * i]     = fin[2 * inOff];
                fout[2 * i + 1] = fin[2 * inOff + 1];
            }
        }
        else
        {
            // Each child sees the input at stride fstride·p; kf_work passes it via factors.
            for (int i = 0; i < p; i++, inOff += step)
                Work(fout.Slice(2 * i * m, 2 * m), fin, inOff, inStride, depth + 1);
        }

        switch (p)
        {
            case 2: Bfly2(fout, st); break;
            case 3: Bfly3(fout, st, _tw); break;
            case 4: Bfly4(fout, st, Inverse); break;
            case 5: Bfly5(fout, st, _tw); break;
            default: BflyGeneric(fout, st, _tw, Length); break;
        }
    }

    // ── Leaf stages (m == 1, unit twiddle) ───────────────────────────────────────

    // kf_bfly2 with m = 1:  F0 = x0 + x1,  F1 = x0 − x1.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Leaf2(Span<double> fout, ReadOnlySpan<double> fin, int i0, int step)
    {
        int i1 = i0 + step;
        double x0r = fin[i0], x0i = fin[i0 + 1], x1r = fin[i1], x1i = fin[i1 + 1];
        fout[3] = x0i - x1i;
        fout[2] = x0r - x1r;
        fout[1] = x0i + x1i;
        fout[0] = x0r + x1r;
    }

    // kf_bfly4 with m = 1 (see the formula block above Bfly4, with tw1 = tw2 = tw3 = 1).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Leaf4(Span<double> fout, ReadOnlySpan<double> fin, int i0, int step, bool inverse)
    {
        int i1 = i0 + step, i2 = i1 + step, i3 = i2 + step;
        double x0r = fin[i0], x0i = fin[i0 + 1];
        double x1r = fin[i1], x1i = fin[i1 + 1];
        double x2r = fin[i2], x2i = fin[i2 + 1];
        double x3r = fin[i3], x3i = fin[i3 + 1];
        double s3r = x0r - x2r, s3i = x0i - x2i;
        double f0r = x0r + x2r, f0i = x0i + x2i;
        double s1r = x1r + x3r, s1i = x1i + x3i;
        double s2r = x1r - x3r, s2i = x1i - x3i;
        // forward: F1 = s3 + (s2.i, −s2.r); inverse: F1 = s3 + (−s2.i, s2.r); F3 mirrors F1.
        double rr = inverse ? -s2i : s2i, ri = inverse ? s2r : -s2r;
        fout[7] = s3i - ri; fout[6] = s3r - rr;   // F3
        fout[5] = f0i - s1i; fout[4] = f0r - s1r; // F2
        fout[3] = s3i + ri; fout[2] = s3r + rr;   // F1
        fout[1] = f0i + s1i; fout[0] = f0r + s1r; // F0
    }

    // ── SIMD helpers ─────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<double> Swap(Vector128<double> x) => Vector128.Shuffle(x, Vector128.Create(1L, 0L));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> Swap(Vector256<double> x) => Vector256.Shuffle(x, Vector256.Create(1L, 0L, 3L, 2L));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<double> Swap(Vector512<double> x) => Vector512.Shuffle(x, Vector512.Create(1L, 0L, 3L, 2L, 5L, 4L, 7L, 6L));

    // x·tw with tw given as the (C, S) table pair.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<double> Mul(Vector128<double> x, Vector128<double> c, Vector128<double> s) => x * c + Swap(x) * s;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> Mul(Vector256<double> x, Vector256<double> c, Vector256<double> s) => x * c + Swap(x) * s;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<double> Mul(Vector512<double> x, Vector512<double> c, Vector512<double> s) => x * c + Swap(x) * s;

    // ── kf_bfly2 ─────────────────────────────────────────────────────────────────

    private static void Bfly2(Span<double> fout, Stage st)
    {
        int m = st.M;
        Debug.Assert(fout.Length == 4 * m && st.TwC.Length == 2 * m);
        ref double f = ref MemoryMarshal.GetReference(fout);
        ref double c = ref MemoryMarshal.GetArrayDataReference(st.TwC);
        ref double s = ref MemoryMarshal.GetArrayDataReference(st.TwS);
        nuint o2 = (nuint)(2 * m), end = (nuint)(2 * m), j = 0;

        if (Vector512.IsHardwareAccelerated)
        {
            for (; j + 8 <= end; j += 8)
            {
                var u = Vector512.LoadUnsafe(ref f, j);
                var t = Mul(Vector512.LoadUnsafe(ref f, j + o2), Vector512.LoadUnsafe(ref c, j), Vector512.LoadUnsafe(ref s, j));
                (u - t).StoreUnsafe(ref f, j + o2);
                (u + t).StoreUnsafe(ref f, j);
            }
        }
        if (Vector256.IsHardwareAccelerated)
        {
            for (; j + 4 <= end; j += 4)
            {
                var u = Vector256.LoadUnsafe(ref f, j);
                var t = Mul(Vector256.LoadUnsafe(ref f, j + o2), Vector256.LoadUnsafe(ref c, j), Vector256.LoadUnsafe(ref s, j));
                (u - t).StoreUnsafe(ref f, j + o2);
                (u + t).StoreUnsafe(ref f, j);
            }
        }
        if (Vector128.IsHardwareAccelerated)
        {
            for (; j < end; j += 2)
            {
                var u = Vector128.LoadUnsafe(ref f, j);
                var t = Mul(Vector128.LoadUnsafe(ref f, j + o2), Vector128.LoadUnsafe(ref c, j), Vector128.LoadUnsafe(ref s, j));
                (u - t).StoreUnsafe(ref f, j + o2);
                (u + t).StoreUnsafe(ref f, j);
            }
        }

        var tc = st.TwC; var ts = st.TwS;
        for (int i = (int)j, i2 = (int)(j + o2); i < (int)end; i += 2, i2 += 2)
        {
            double xr = fout[i2], xi = fout[i2 + 1];
            double tr = xr * tc[i] + xi * ts[i];
            double ti = xi * tc[i + 1] + xr * ts[i + 1];
            fout[i2]     = fout[i] - tr;
            fout[i2 + 1] = fout[i + 1] - ti;
            fout[i]     += tr;
            fout[i + 1] += ti;
        }
    }

    // ── kf_bfly4 ─────────────────────────────────────────────────────────────────
    //   s0 = F1·tw1, s1 = F2·tw2, s2 = F3·tw3
    //   s3 = F0 − s1;  F0 += s1;  s1' = s0 + s2;  s2' = s0 − s2
    //   F2 = F0 − s1'; F0 += s1'
    //   forward: F1 = s3 + (s2'.i, −s2'.r);  F3 = s3 − (s2'.i, −s2'.r)
    //   inverse: rotation sign flipped.

    private static void Bfly4(Span<double> fout, Stage st, bool inverse)
    {
        int m = st.M;
        Debug.Assert(fout.Length == 8 * m && st.TwC.Length == 6 * m);
        ref double f = ref MemoryMarshal.GetReference(fout);
        ref double c = ref MemoryMarshal.GetArrayDataReference(st.TwC);
        ref double s = ref MemoryMarshal.GetArrayDataReference(st.TwS);
        nuint o1 = (nuint)(2 * m), o2 = 2 * o1, o3 = 3 * o1, end = o1, j = 0;
        double rotR = inverse ? -1.0 : 1.0;   // multiplies s2'.i into the real part
        double rotI = -rotR;                  // multiplies s2'.r into the imaginary part

        if (Vector512.IsHardwareAccelerated)
        {
            var rot = Vector512.Create(rotR, rotI, rotR, rotI, rotR, rotI, rotR, rotI);
            for (; j + 8 <= end; j += 8)
            {
                var x0 = Vector512.LoadUnsafe(ref f, j);
                var a  = Mul(Vector512.LoadUnsafe(ref f, j + o1), Vector512.LoadUnsafe(ref c, j),      Vector512.LoadUnsafe(ref s, j));
                var b  = Mul(Vector512.LoadUnsafe(ref f, j + o2), Vector512.LoadUnsafe(ref c, j + o1), Vector512.LoadUnsafe(ref s, j + o1));
                var d  = Mul(Vector512.LoadUnsafe(ref f, j + o3), Vector512.LoadUnsafe(ref c, j + o2), Vector512.LoadUnsafe(ref s, j + o2));
                var s3 = x0 - b;
                var f0 = x0 + b;
                var s1 = a + d;
                var s2 = a - d;
                (f0 - s1).StoreUnsafe(ref f, j + o2);
                (f0 + s1).StoreUnsafe(ref f, j);
                var r = Swap(s2) * rot;
                (s3 + r).StoreUnsafe(ref f, j + o1);
                (s3 - r).StoreUnsafe(ref f, j + o3);
            }
        }
        if (Vector256.IsHardwareAccelerated)
        {
            var rot = Vector256.Create(rotR, rotI, rotR, rotI);
            for (; j + 4 <= end; j += 4)
            {
                var x0 = Vector256.LoadUnsafe(ref f, j);
                var a  = Mul(Vector256.LoadUnsafe(ref f, j + o1), Vector256.LoadUnsafe(ref c, j),      Vector256.LoadUnsafe(ref s, j));
                var b  = Mul(Vector256.LoadUnsafe(ref f, j + o2), Vector256.LoadUnsafe(ref c, j + o1), Vector256.LoadUnsafe(ref s, j + o1));
                var d  = Mul(Vector256.LoadUnsafe(ref f, j + o3), Vector256.LoadUnsafe(ref c, j + o2), Vector256.LoadUnsafe(ref s, j + o2));
                var s3 = x0 - b;
                var f0 = x0 + b;
                var s1 = a + d;
                var s2 = a - d;
                (f0 - s1).StoreUnsafe(ref f, j + o2);
                (f0 + s1).StoreUnsafe(ref f, j);
                var r = Swap(s2) * rot;
                (s3 + r).StoreUnsafe(ref f, j + o1);
                (s3 - r).StoreUnsafe(ref f, j + o3);
            }
        }
        if (Vector128.IsHardwareAccelerated)
        {
            var rot = Vector128.Create(rotR, rotI);
            for (; j < end; j += 2)
            {
                var x0 = Vector128.LoadUnsafe(ref f, j);
                var a  = Mul(Vector128.LoadUnsafe(ref f, j + o1), Vector128.LoadUnsafe(ref c, j),      Vector128.LoadUnsafe(ref s, j));
                var b  = Mul(Vector128.LoadUnsafe(ref f, j + o2), Vector128.LoadUnsafe(ref c, j + o1), Vector128.LoadUnsafe(ref s, j + o1));
                var d  = Mul(Vector128.LoadUnsafe(ref f, j + o3), Vector128.LoadUnsafe(ref c, j + o2), Vector128.LoadUnsafe(ref s, j + o2));
                var s3 = x0 - b;
                var f0 = x0 + b;
                var s1 = a + d;
                var s2 = a - d;
                (f0 - s1).StoreUnsafe(ref f, j + o2);
                (f0 + s1).StoreUnsafe(ref f, j);
                var r = Swap(s2) * rot;
                (s3 + r).StoreUnsafe(ref f, j + o1);
                (s3 - r).StoreUnsafe(ref f, j + o3);
            }
        }

        var tc = st.TwC; var ts = st.TwS;
        int n1 = (int)o1;
        for (int i = (int)j; i < (int)end; i += 2)
        {
            int i1 = i + n1, i2 = i1 + n1, i3 = i2 + n1;
            double ar = fout[i1] * tc[i] + fout[i1 + 1] * ts[i];
            double ai = fout[i1 + 1] * tc[i + 1] + fout[i1] * ts[i + 1];
            double br = fout[i2] * tc[i1] + fout[i2 + 1] * ts[i1];
            double bi = fout[i2 + 1] * tc[i1 + 1] + fout[i2] * ts[i1 + 1];
            double dr = fout[i3] * tc[i2] + fout[i3 + 1] * ts[i2];
            double di = fout[i3 + 1] * tc[i2 + 1] + fout[i3] * ts[i2 + 1];
            double x0r = fout[i], x0i = fout[i + 1];
            double s3r = x0r - br, s3i = x0i - bi;
            double f0r = x0r + br, f0i = x0i + bi;
            double s1r = ar + dr,  s1i = ai + di;
            double s2r = ar - dr,  s2i = ai - di;
            fout[i2] = f0r - s1r; fout[i2 + 1] = f0i - s1i;
            fout[i]  = f0r + s1r; fout[i + 1]  = f0i + s1i;
            double rr = s2i * rotR, ri = s2r * rotI;
            fout[i1] = s3r + rr; fout[i1 + 1] = s3i + ri;
            fout[i3] = s3r - rr; fout[i3 + 1] = s3i - ri;
        }
    }

    // ── kf_bfly3 ─────────────────────────────────────────────────────────────────
    //   s1 = F1·tw1, s2 = F2·tw2;  s3 = s1 + s2;  s0 = s1 − s2
    //   F1 = F0 − s3/2;  s0 *= ε.i;  F0 += s3
    //   F2 = F1 + (s0.i, −s0.r);  F1 = F1 − (s0.i, −s0.r)

    private static void Bfly3(Span<double> fout, Stage st, double[] tw)
    {
        int m = st.M;
        Debug.Assert(fout.Length == 6 * m && st.TwC.Length == 4 * m);
        ref double f = ref MemoryMarshal.GetReference(fout);
        ref double c = ref MemoryMarshal.GetArrayDataReference(st.TwC);
        ref double s = ref MemoryMarshal.GetArrayDataReference(st.TwS);
        nuint o1 = (nuint)(2 * m), o2 = 2 * o1, end = o1, j = 0;
        double epsI = tw[2 * st.FStride * m + 1];

        if (Vector256.IsHardwareAccelerated)
        {
            var half = Vector256.Create(0.5);
            var eps  = Vector256.Create(epsI);
            var rot  = Vector256.Create(1.0, -1.0, 1.0, -1.0);
            for (; j + 4 <= end; j += 4)
            {
                var x0 = Vector256.LoadUnsafe(ref f, j);
                var a  = Mul(Vector256.LoadUnsafe(ref f, j + o1), Vector256.LoadUnsafe(ref c, j),      Vector256.LoadUnsafe(ref s, j));
                var b  = Mul(Vector256.LoadUnsafe(ref f, j + o2), Vector256.LoadUnsafe(ref c, j + o1), Vector256.LoadUnsafe(ref s, j + o1));
                var s3 = a + b;
                var s0 = (a - b) * eps;
                var f1 = x0 - s3 * half;
                (x0 + s3).StoreUnsafe(ref f, j);
                var r = Swap(s0) * rot;
                (f1 + r).StoreUnsafe(ref f, j + o2);
                (f1 - r).StoreUnsafe(ref f, j + o1);
            }
        }
        if (Vector128.IsHardwareAccelerated)
        {
            var half = Vector128.Create(0.5);
            var eps  = Vector128.Create(epsI);
            var rot  = Vector128.Create(1.0, -1.0);
            for (; j < end; j += 2)
            {
                var x0 = Vector128.LoadUnsafe(ref f, j);
                var a  = Mul(Vector128.LoadUnsafe(ref f, j + o1), Vector128.LoadUnsafe(ref c, j),      Vector128.LoadUnsafe(ref s, j));
                var b  = Mul(Vector128.LoadUnsafe(ref f, j + o2), Vector128.LoadUnsafe(ref c, j + o1), Vector128.LoadUnsafe(ref s, j + o1));
                var s3 = a + b;
                var s0 = (a - b) * eps;
                var f1 = x0 - s3 * half;
                (x0 + s3).StoreUnsafe(ref f, j);
                var r = Swap(s0) * rot;
                (f1 + r).StoreUnsafe(ref f, j + o2);
                (f1 - r).StoreUnsafe(ref f, j + o1);
            }
        }

        var tc = st.TwC; var ts = st.TwS;
        int n1 = (int)o1;
        for (int i = (int)j; i < (int)end; i += 2)
        {
            int i1 = i + n1, i2 = i1 + n1;
            double ar = fout[i1] * tc[i] + fout[i1 + 1] * ts[i];
            double ai = fout[i1 + 1] * tc[i + 1] + fout[i1] * ts[i + 1];
            double br = fout[i2] * tc[i1] + fout[i2 + 1] * ts[i1];
            double bi = fout[i2 + 1] * tc[i1 + 1] + fout[i2] * ts[i1 + 1];
            double s3r = ar + br, s3i = ai + bi;
            double s0r = (ar - br) * epsI, s0i = (ai - bi) * epsI;
            double x0r = fout[i], x0i = fout[i + 1];
            double f1r = x0r - s3r * 0.5, f1i = x0i - s3i * 0.5;
            fout[i] = x0r + s3r; fout[i + 1] = x0i + s3i;
            double rr = s0i * 1.0, ri = s0r * -1.0;
            fout[i2] = f1r + rr; fout[i2 + 1] = f1i + ri;
            fout[i1] = f1r - rr; fout[i1 + 1] = f1i - ri;
        }
    }

    // ── kf_bfly5 ─────────────────────────────────────────────────────────────────
    //   s1..s4 = F1..F4 · tw1..tw4
    //   s7 = s1 + s4, s10 = s1 − s4, s8 = s2 + s3, s9 = s2 − s3
    //   F0 += s7 + s8
    //   s5  = s0 + s7·ya.r + s8·yb.r;  s6  = ( s10.i·ya.i + s9.i·yb.i, −s10.r·ya.i − s9.r·yb.i)
    //   F1 = s5 − s6;  F4 = s5 + s6
    //   s11 = s0 + s7·yb.r + s8·ya.r;  s12 = (−s10.i·yb.i + s9.i·ya.i,  s10.r·yb.i − s9.r·ya.i)
    //   F2 = s11 + s12; F3 = s11 − s12

    private static void Bfly5(Span<double> fout, Stage st, double[] tw)
    {
        int m = st.M;
        Debug.Assert(fout.Length == 10 * m && st.TwC.Length == 8 * m);
        ref double f = ref MemoryMarshal.GetReference(fout);
        ref double c = ref MemoryMarshal.GetArrayDataReference(st.TwC);
        ref double s = ref MemoryMarshal.GetArrayDataReference(st.TwS);
        nuint o1 = (nuint)(2 * m), o2 = 2 * o1, o3 = 3 * o1, o4 = 4 * o1, end = o1, j = 0;
        double yaR = tw[2 * st.FStride * m],     yaI = tw[2 * st.FStride * m + 1];
        double ybR = tw[4 * st.FStride * m],     ybI = tw[4 * st.FStride * m + 1];

        if (Vector256.IsHardwareAccelerated)
        {
            var vyaR = Vector256.Create(yaR);
            var vybR = Vector256.Create(ybR);
            var yaA  = Vector256.Create(yaI, -yaI, yaI, -yaI);
            var ybA  = Vector256.Create(ybI, -ybI, ybI, -ybI);
            var ybB  = Vector256.Create(-ybI, ybI, -ybI, ybI);
            for (; j + 4 <= end; j += 4)
            {
                var s0 = Vector256.LoadUnsafe(ref f, j);
                var s1 = Mul(Vector256.LoadUnsafe(ref f, j + o1), Vector256.LoadUnsafe(ref c, j),      Vector256.LoadUnsafe(ref s, j));
                var s2 = Mul(Vector256.LoadUnsafe(ref f, j + o2), Vector256.LoadUnsafe(ref c, j + o1), Vector256.LoadUnsafe(ref s, j + o1));
                var s3 = Mul(Vector256.LoadUnsafe(ref f, j + o3), Vector256.LoadUnsafe(ref c, j + o2), Vector256.LoadUnsafe(ref s, j + o2));
                var s4 = Mul(Vector256.LoadUnsafe(ref f, j + o4), Vector256.LoadUnsafe(ref c, j + o3), Vector256.LoadUnsafe(ref s, j + o3));
                var s7  = s1 + s4;
                var s10 = s1 - s4;
                var s8  = s2 + s3;
                var s9  = s2 - s3;
                (s0 + (s7 + s8)).StoreUnsafe(ref f, j);
                var w10 = Swap(s10);
                var w9  = Swap(s9);
                var s5  = s0 + s7 * vyaR + s8 * vybR;
                var s6  = w10 * yaA + w9 * ybA;
                (s5 - s6).StoreUnsafe(ref f, j + o1);
                (s5 + s6).StoreUnsafe(ref f, j + o4);
                var s11 = s0 + s7 * vybR + s8 * vyaR;
                var s12 = w10 * ybB + w9 * yaA;
                (s11 + s12).StoreUnsafe(ref f, j + o2);
                (s11 - s12).StoreUnsafe(ref f, j + o3);
            }
        }
        if (Vector128.IsHardwareAccelerated)
        {
            var vyaR = Vector128.Create(yaR);
            var vybR = Vector128.Create(ybR);
            var yaA  = Vector128.Create(yaI, -yaI);
            var ybA  = Vector128.Create(ybI, -ybI);
            var ybB  = Vector128.Create(-ybI, ybI);
            for (; j < end; j += 2)
            {
                var s0 = Vector128.LoadUnsafe(ref f, j);
                var s1 = Mul(Vector128.LoadUnsafe(ref f, j + o1), Vector128.LoadUnsafe(ref c, j),      Vector128.LoadUnsafe(ref s, j));
                var s2 = Mul(Vector128.LoadUnsafe(ref f, j + o2), Vector128.LoadUnsafe(ref c, j + o1), Vector128.LoadUnsafe(ref s, j + o1));
                var s3 = Mul(Vector128.LoadUnsafe(ref f, j + o3), Vector128.LoadUnsafe(ref c, j + o2), Vector128.LoadUnsafe(ref s, j + o2));
                var s4 = Mul(Vector128.LoadUnsafe(ref f, j + o4), Vector128.LoadUnsafe(ref c, j + o3), Vector128.LoadUnsafe(ref s, j + o3));
                var s7  = s1 + s4;
                var s10 = s1 - s4;
                var s8  = s2 + s3;
                var s9  = s2 - s3;
                (s0 + (s7 + s8)).StoreUnsafe(ref f, j);
                var w10 = Swap(s10);
                var w9  = Swap(s9);
                var s5  = s0 + s7 * vyaR + s8 * vybR;
                var s6  = w10 * yaA + w9 * ybA;
                (s5 - s6).StoreUnsafe(ref f, j + o1);
                (s5 + s6).StoreUnsafe(ref f, j + o4);
                var s11 = s0 + s7 * vybR + s8 * vyaR;
                var s12 = w10 * ybB + w9 * yaA;
                (s11 + s12).StoreUnsafe(ref f, j + o2);
                (s11 - s12).StoreUnsafe(ref f, j + o3);
            }
        }

        var tc = st.TwC; var ts = st.TwS;
        int n1 = (int)o1;
        for (int i = (int)j; i < (int)end; i += 2)
        {
            int i1 = i + n1, i2 = i1 + n1, i3 = i2 + n1, i4 = i3 + n1;
            double s1r = fout[i1] * tc[i]  + fout[i1 + 1] * ts[i],  s1i = fout[i1 + 1] * tc[i + 1]  + fout[i1] * ts[i + 1];
            double s2r = fout[i2] * tc[i1] + fout[i2 + 1] * ts[i1], s2i = fout[i2 + 1] * tc[i1 + 1] + fout[i2] * ts[i1 + 1];
            double s3r = fout[i3] * tc[i2] + fout[i3 + 1] * ts[i2], s3i = fout[i3 + 1] * tc[i2 + 1] + fout[i3] * ts[i2 + 1];
            double s4r = fout[i4] * tc[i3] + fout[i4 + 1] * ts[i3], s4i = fout[i4 + 1] * tc[i3 + 1] + fout[i4] * ts[i3 + 1];
            double s0r = fout[i], s0i = fout[i + 1];
            double s7r = s1r + s4r, s7i = s1i + s4i;
            double s10r = s1r - s4r, s10i = s1i - s4i;
            double s8r = s2r + s3r, s8i = s2i + s3i;
            double s9r = s2r - s3r, s9i = s2i - s3i;
            fout[i] = s0r + (s7r + s8r); fout[i + 1] = s0i + (s7i + s8i);
            double s5r = s0r + s7r * yaR + s8r * ybR, s5i = s0i + s7i * yaR + s8i * ybR;
            double s6r = s10i * yaI + s9i * ybI,      s6i = s10r * -yaI + s9r * -ybI;
            fout[i1] = s5r - s6r; fout[i1 + 1] = s5i - s6i;
            fout[i4] = s5r + s6r; fout[i4 + 1] = s5i + s6i;
            double s11r = s0r + s7r * ybR + s8r * yaR, s11i = s0i + s7i * ybR + s8i * yaR;
            double s12r = s10i * -ybI + s9i * yaI,     s12i = s10r * ybI + s9r * -yaI;
            fout[i2] = s11r + s12r; fout[i2 + 1] = s11i + s12i;
            fout[i3] = s11r - s12r; fout[i3 + 1] = s11i - s12i;
        }
    }

    // ── kf_bfly_generic ──────────────────────────────────────────────────────────

    private static void BflyGeneric(Span<double> fout, Stage st, double[] tw, int nOrig)
    {
        int p = st.P, m = st.M, fstride = st.FStride;
        double[]? rented = null;
        Span<double> scratch = p <= 64
            ? stackalloc double[2 * p]
            : (rented = ArrayPool<double>.Shared.Rent(2 * p)).AsSpan(0, 2 * p);

        for (int u = 0; u < m; u++)
        {
            for (int q1 = 0, k = u; q1 < p; q1++, k += m)
            {
                scratch[2 * q1]     = fout[2 * k];
                scratch[2 * q1 + 1] = fout[2 * k + 1];
            }

            for (int q1 = 0, k = u; q1 < p; q1++, k += m)
            {
                int twidx = 0;
                double accR = scratch[0], accI = scratch[1];
                for (int q = 1; q < p; q++)
                {
                    twidx += fstride * k;
                    if (twidx >= nOrig) twidx -= nOrig;
                    double xr = scratch[2 * q], xi = scratch[2 * q + 1];
                    double cr = tw[2 * twidx], ci = tw[2 * twidx + 1];
                    accR += xr * cr - xi * ci;
                    accI += xr * ci + xi * cr;
                }
                fout[2 * k]     = accR;
                fout[2 * k + 1] = accI;
            }
        }

        if (rented is not null) ArrayPool<double>.Shared.Return(rented);
    }
}

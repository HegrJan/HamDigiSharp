// C# port of erf from fdlibm s_erf.c (http://www.netlib.org/fdlibm/).
//
// Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved.
//
// Developed at SunPro, a Sun Microsystems, Inc. business.
// Permission to use, copy, modify, and distribute this
// software is freely granted, provided that this notice
// is preserved.

namespace HamDigiSharp.Dsp;

/// <summary>
/// Special functions not provided by <see cref="Math"/>.
/// </summary>
internal static class SpecialMath
{
    private const double Tiny = 1e-300;
    private const double Erx  = 8.45062911510467529297e-01;
    // Coefficients for approximation to erf on [0, 0.84375]
    private const double Efx  = 1.28379167095512586316e-01;
    private const double Efx8 = 1.02703333676410069053e+00;
    private const double Pp0  = 1.28379167095512558561e-01;
    private const double Pp1  = -3.25042107247001499370e-01;
    private const double Pp2  = -2.84817495755985104766e-02;
    private const double Pp3  = -5.77027029648944159157e-03;
    private const double Pp4  = -2.37630166566501626084e-05;
    private const double Qq1  = 3.97917223959155352819e-01;
    private const double Qq2  = 6.50222499887672944485e-02;
    private const double Qq3  = 5.08130628187576562776e-03;
    private const double Qq4  = 1.32494738004321644526e-04;
    private const double Qq5  = -3.96022827877536812320e-06;
    // Coefficients for approximation to erf in [0.84375, 1.25]
    private const double Pa0  = -2.36211856075265944077e-03;
    private const double Pa1  = 4.14856118683748331666e-01;
    private const double Pa2  = -3.72207876035701323847e-01;
    private const double Pa3  = 3.18346619901161753674e-01;
    private const double Pa4  = -1.10894694282396677476e-01;
    private const double Pa5  = 3.54783043256182359371e-02;
    private const double Pa6  = -2.16637559486879084300e-03;
    private const double Qa1  = 1.06420880400844228286e-01;
    private const double Qa2  = 5.40397917702171048937e-01;
    private const double Qa3  = 7.18286544141962662868e-02;
    private const double Qa4  = 1.26171219808761642112e-01;
    private const double Qa5  = 1.36370839120290507362e-02;
    private const double Qa6  = 1.19844998467991074170e-02;
    // Coefficients for approximation to erfc in [1.25, 1/0.35]
    private const double Ra0  = -9.86494403484714822705e-03;
    private const double Ra1  = -6.93858572707181764372e-01;
    private const double Ra2  = -1.05586262253232909814e+01;
    private const double Ra3  = -6.23753324503260060396e+01;
    private const double Ra4  = -1.62396669462573470355e+02;
    private const double Ra5  = -1.84605092906711035994e+02;
    private const double Ra6  = -8.12874355063065934246e+01;
    private const double Ra7  = -9.81432934416914548592e+00;
    private const double Sa1  = 1.96512716674392571292e+01;
    private const double Sa2  = 1.37657754143519042600e+02;
    private const double Sa3  = 4.34565877475229228821e+02;
    private const double Sa4  = 6.45387271733267880336e+02;
    private const double Sa5  = 4.29008140027567833386e+02;
    private const double Sa6  = 1.08635005541779435134e+02;
    private const double Sa7  = 6.57024977031928170135e+00;
    private const double Sa8  = -6.04244152148580987438e-02;
    // Coefficients for approximation to erfc in [1/0.35, 28]
    private const double Rb0  = -9.86494292470009928597e-03;
    private const double Rb1  = -7.99283237680523006574e-01;
    private const double Rb2  = -1.77579549177547519889e+01;
    private const double Rb3  = -1.60636384855821916062e+02;
    private const double Rb4  = -6.37566443368389627722e+02;
    private const double Rb5  = -1.02509513161107724954e+03;
    private const double Rb6  = -4.83519191608651397019e+02;
    private const double Sb1  = 3.03380607434824582924e+01;
    private const double Sb2  = 3.25792512996573918826e+02;
    private const double Sb3  = 1.53672958608443695994e+03;
    private const double Sb4  = 3.19985821950859553908e+03;
    private const double Sb5  = 2.55305040643316442583e+03;
    private const double Sb6  = 4.74528541206955367215e+02;
    private const double Sb7  = -2.24409524465858183362e+01;

    /// <summary>
    /// Error function erf(x) = 2/√π ∫₀ˣ e^{-t²} dt. Port of fdlibm (the implementation
    /// behind glibc and gfortran's <c>erf</c> intrinsic used by WSJT-X's gfsk_pulse).
    /// </summary>
    public static double Erf(double x)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);
        int hx = (int)(bits >> 32);
        int ix = hx & 0x7fffffff;

        if (ix >= 0x7ff00000)
        {
            // erf(NaN) = NaN, erf(±Inf) = ±1
            int i = (int)((uint)hx >> 31) << 1;
            return (1 - i) + 1.0 / x;
        }

        if (ix < 0x3feb0000)
        {
            // |x| < 0.84375
            if (ix < 0x3e300000)
            {
                // |x| < 2**-28
                if (ix < 0x00800000) return 0.125 * (8.0 * x + Efx8 * x); // avoid underflow
                return x + Efx * x;
            }
            double z = x * x;
            double r = Pp0 + z * (Pp1 + z * (Pp2 + z * (Pp3 + z * Pp4)));
            double s = 1.0 + z * (Qq1 + z * (Qq2 + z * (Qq3 + z * (Qq4 + z * Qq5))));
            return x + x * (r / s);
        }

        if (ix < 0x3ff40000)
        {
            // 0.84375 <= |x| < 1.25
            double s = Math.Abs(x) - 1.0;
            double p = Pa0 + s * (Pa1 + s * (Pa2 + s * (Pa3 + s * (Pa4 + s * (Pa5 + s * Pa6)))));
            double q = 1.0 + s * (Qa1 + s * (Qa2 + s * (Qa3 + s * (Qa4 + s * (Qa5 + s * Qa6)))));
            return hx >= 0 ? Erx + p / q : -Erx - p / q;
        }

        if (ix >= 0x40180000)
        {
            // |x| >= 6
            return hx >= 0 ? 1.0 - Tiny : Tiny - 1.0;
        }

        double ax = Math.Abs(x);
        double ss = 1.0 / (ax * ax);
        double rr, sr;
        if (ix < 0x4006DB6E)
        {
            // |x| < 1/0.35
            rr = Ra0 + ss * (Ra1 + ss * (Ra2 + ss * (Ra3 + ss * (Ra4 + ss * (Ra5 + ss * (Ra6 + ss * Ra7))))));
            sr = 1.0 + ss * (Sa1 + ss * (Sa2 + ss * (Sa3 + ss * (Sa4 + ss * (Sa5 + ss * (Sa6 + ss * (Sa7 + ss * Sa8)))))));
        }
        else
        {
            // 1/0.35 <= |x| < 6
            rr = Rb0 + ss * (Rb1 + ss * (Rb2 + ss * (Rb3 + ss * (Rb4 + ss * (Rb5 + ss * Rb6)))));
            sr = 1.0 + ss * (Sb1 + ss * (Sb2 + ss * (Sb3 + ss * (Sb4 + ss * (Sb5 + ss * (Sb6 + ss * Sb7))))));
        }
        // z = |x| with the low 32 bits cleared, splitting exp(-x²) for accuracy.
        double zz = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(ax) & ~0xffffffffL);
        double e = Math.Exp(-zz * zz - 0.5625) * Math.Exp((zz - ax) * (zz + ax) + rr / sr);
        return hx >= 0 ? 1.0 - e / ax : e / ax - 1.0;
    }
}

using System.Numerics;

namespace HamDigiSharp.Dsp;

internal static class ComplexExtensions
{
    extension(Complex c)
    {
        /// <summary>|c|² — avoids the hypot/sqrt in <see cref="Complex.Magnitude"/>.</summary>
        public double MagnitudeSquared => c.Real * c.Real + c.Imaginary * c.Imaginary;
    }
}

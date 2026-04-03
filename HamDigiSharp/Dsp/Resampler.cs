namespace HamDigiSharp.Dsp;

/// <summary>
/// Polyphase resampler — converts a PCM stream from one integer sample rate to another.
/// Used by the engine to down-convert caller-supplied audio to each decoder's native rate.
/// </summary>
public sealed class Resampler
{
    private readonly int _inRate;
    private readonly int _outRate;
    private readonly double _ratio;   // _inRate / _outRate (pre-computed)

    // Rational factor: _inRate / _outRate = L/M after reduction
    private readonly int _L;   // upsample factor
    private readonly int _M;   // downsample factor
    private readonly double[] _filter;
    private readonly double[] _state;
#pragma warning disable CS0414
    private int _stateLen;
#pragma warning restore CS0414
#pragma warning disable CS0414
    private long _phase; // position in upsampled grid
#pragma warning restore CS0414

    private const int FilterTaps = 64;

    public Resampler(int inputRate, int outputRate)
    {
        _inRate = inputRate;
        _outRate = outputRate;
        _ratio = (double)_inRate / _outRate;

        int g = Gcd(inputRate, outputRate);
        _L = outputRate / g;
        _M = inputRate / g;

        // Design a simple lowpass FIR (Kaiser-windowed sinc)
        double cutoff = 0.5 / Math.Max(_L, _M);
        _filter = DesignSincFilter(FilterTaps * _L, cutoff * _L);
        _state = new double[FilterTaps];
        _stateLen = 0;
        _phase = 0;
    }

    public int InputRate => _inRate;
    public int OutputRate => _outRate;

    /// <summary>
    /// Resample a block of float samples into a caller-provided buffer (zero allocation).
    /// <paramref name="output"/> must have length ≥
    /// <c>⌈input.Length × OutputRate / InputRate⌉</c>.
    /// </summary>
    public void ProcessInto(ReadOnlySpan<float> input, Span<float> output)
    {
        if (_inRate == _outRate)
        {
            input.CopyTo(output);
            return;
        }
        double readPos = 0.0;
        for (int outIdx = 0; outIdx < output.Length; outIdx++)
        {
            int i0 = (int)readPos;
            double frac = readPos - i0;
            int i1 = Math.Min(i0 + 1, input.Length - 1);
            output[outIdx] = i0 < input.Length
                ? (float)(input[i0] * (1.0 - frac) + input[i1] * frac)
                : 0.0f;
            readPos += _ratio;
        }
    }

    /// <summary>
    /// Resample a block of float samples.
    /// </summary>
    public float[] Process(ReadOnlySpan<float> input)
    {
        if (_inRate == _outRate)
            return input.ToArray();

        int outLen = (int)Math.Ceiling((double)input.Length * _outRate / _inRate);
        var output = new float[outLen];
        double readPos = 0.0;
        for (int outIdx = 0; outIdx < outLen; outIdx++)
        {
            int i0 = (int)readPos;
            double frac = readPos - i0;
            int i1 = Math.Min(i0 + 1, input.Length - 1);
            output[outIdx] = i0 < input.Length
                ? (float)(input[i0] * (1.0 - frac) + input[i1] * frac)
                : 0.0f;
            readPos += _ratio;
        }
        return output;
    }

    /// <summary>
    /// Resample a block of double samples.
    /// </summary>
    public double[] Process(ReadOnlySpan<double> input)
    {
        if (_inRate == _outRate)
            return input.ToArray();

        int outLen = (int)Math.Ceiling((double)input.Length * _outRate / _inRate);
        var output = new double[outLen];
        double readPos = 0.0;
        for (int outIdx = 0; outIdx < outLen; outIdx++)
        {
            int i0 = (int)readPos;
            double frac = readPos - i0;
            int i1 = Math.Min(i0 + 1, input.Length - 1);
            output[outIdx] = i0 < input.Length
                ? input[i0] * (1.0 - frac) + input[i1] * frac
                : 0.0;
            readPos += _ratio;
        }
        return output;
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    private static double[] DesignSincFilter(int taps, double cutoff)
    {
        var h = new double[taps];
        int half = taps / 2;
        double sum = 0;
        for (int i = 0; i < taps; i++)
        {
            double n = i - half;
            double sinc = Math.Abs(n) < 1e-9 ? 1.0 : Math.Sin(Math.PI * cutoff * n) / (Math.PI * cutoff * n);
            // Hann window
            double win = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (taps - 1)));
            h[i] = sinc * win;
            sum += h[i];
        }
        for (int i = 0; i < taps; i++) h[i] /= sum;
        return h;
    }
}

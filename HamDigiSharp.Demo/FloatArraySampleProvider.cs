using NAudio.Wave;

namespace HamDigiSharp.Demo;

/// <summary>
/// Plays back a float[] PCM buffer once.
/// Call <see cref="BeginFadeOut"/> to ramp amplitude to silence; the provider
/// then returns 0 (end-of-stream) so WaveOut stops via its natural path — no click.
/// </summary>
internal sealed class FloatArraySampleProvider : ISampleProvider
{
    private readonly float[] _data;
    private int _position;
    private int _fadeStart   = -1;
    private int _fadeSamples;

    public WaveFormat WaveFormat { get; }

    public FloatArraySampleProvider(float[] data, int sampleRate, int channels)
    {
        _data      = data;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    /// <summary>Begin a ~40 ms linear fade to silence, then end the stream.</summary>
    public void BeginFadeOut(int fadeSamples = 480)
    {
        _fadeStart   = _position;
        _fadeSamples = fadeSamples;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // Fade complete → signal end-of-stream
        if (_fadeStart >= 0 && _position >= _fadeStart + _fadeSamples) return 0;

        int limit     = _fadeStart >= 0 ? _fadeStart + _fadeSamples : _data.Length;
        int available = Math.Min(count, limit - _position);
        if (available <= 0) return 0;

        Array.Copy(_data, _position, buffer, offset, available);

        if (_fadeStart >= 0)
            for (int i = 0; i < available; i++)
            {
                float gain = 1f - (float)(_position - _fadeStart + i) / _fadeSamples;
                buffer[offset + i] *= MathF.Max(0f, gain);
            }

        _position += available;
        return available;
    }
}

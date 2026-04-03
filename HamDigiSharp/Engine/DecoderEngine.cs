using HamDigiSharp.Abstractions;
using HamDigiSharp.Decoders;
using HamDigiSharp.Decoders.Fsk;
using HamDigiSharp.Decoders.Ft2;
using HamDigiSharp.Decoders.Ft4;
using HamDigiSharp.Decoders.Ft8;
using HamDigiSharp.Decoders.Iscat;
using HamDigiSharp.Decoders.Jt65;
using HamDigiSharp.Decoders.Jt6m;
using HamDigiSharp.Decoders.Jtms;
using HamDigiSharp.Decoders.Msk;
using HamDigiSharp.Decoders.Pi4;
using HamDigiSharp.Decoders.Q65;
using HamDigiSharp.Decoders.SuperFox;
using HamDigiSharp.Models;

namespace HamDigiSharp.Engine;

/// <summary>
/// Top-level public API for the MSHV decoding library.
///
/// Usage:
/// <code>
///   var engine = new DecoderEngine();
///   engine.Configure(new DecoderOptions { MyCall = "W1AW", ... });
///   engine.ResultAvailable += r => Console.WriteLine(r);
///   await engine.DecodeAsync(samples, DigitalMode.FT8, freqLow, freqHigh, utcTime);
/// </code>
///
/// One decoder instance per mode — each already uses PLINQ internally to
/// parallelise across candidates and CPU cores.
/// </summary>
public sealed class DecoderEngine : IDisposable
{
    private readonly Dictionary<DigitalMode, IDigitalModeDecoder> _decoders = new();
    private DecoderOptions _options = new();
    private bool _disposed;

    public event Action<DecodeResult>? ResultAvailable;

    public DecoderEngine()
    {
        // ── FT modes (LDPC-based, 8/4-FSK) ───────────────────────────────────
        Register(DigitalMode.FT8,      new Ft8Decoder());
        Register(DigitalMode.FT4,      new Ft4Decoder());
        Register(DigitalMode.FT2,      new Ft2Decoder());
        Register(DigitalMode.SuperFox, new SuperFoxDecoder());

        // ── JT65 modes (Reed-Solomon, 65-FSK) ────────────────────────────────
        Register(DigitalMode.JT65A,    new Jt65Decoder(DigitalMode.JT65A));
        Register(DigitalMode.JT65B,    new Jt65Decoder(DigitalMode.JT65B));
        Register(DigitalMode.JT65C,    new Jt65Decoder(DigitalMode.JT65C));
        Register(DigitalMode.JT6M,     new Jt6mDecoder());

        // ── Q65 modes (LDPC, 65-FSK, coherent averaging) ─────────────────────
        Register(DigitalMode.Q65A,     new Q65Decoder(DigitalMode.Q65A));
        Register(DigitalMode.Q65B,     new Q65Decoder(DigitalMode.Q65B));
        Register(DigitalMode.Q65C,     new Q65Decoder(DigitalMode.Q65C));
        Register(DigitalMode.Q65D,     new Q65Decoder(DigitalMode.Q65D));

        // ── MSK modes ─────────────────────────────────────────────────────────
        Register(DigitalMode.MSK144,   new Msk144Decoder());
        Register(DigitalMode.MSKMS,    new Msk40Decoder());

        // ── FSK meteor scatter ────────────────────────────────────────────────
        Register(DigitalMode.FSK441,   new Fsk441Decoder());
        Register(DigitalMode.FSK315,   new Fsk315Decoder());

        // ── Other modes ───────────────────────────────────────────────────────
        Register(DigitalMode.IscatA,   new IscatDecoder(DigitalMode.IscatA));
        Register(DigitalMode.IscatB,   new IscatDecoder(DigitalMode.IscatB));
        Register(DigitalMode.PI4,      new Pi4Decoder());
        Register(DigitalMode.JTMS,     new JtmsDecoder());
    }

    // ── Configuration ─────────────────────────────────────────────────────────

    public void Configure(DecoderOptions options)
    {
        _options = options;
        foreach (var dec in _decoders.Values)
            dec.Configure(options);
    }

    // ── Decode ────────────────────────────────────────────────────────────────

    /// <summary>Decode one period of audio for the specified mode asynchronously.</summary>
    public Task<IReadOnlyList<DecodeResult>> DecodeAsync(
        float[] samples,
        DigitalMode mode,
        double freqLow,
        double freqHigh,
        string utcTime,
        CancellationToken cancellationToken = default)
    {
        if (!_decoders.TryGetValue(mode, out var dec))
            return Task.FromResult<IReadOnlyList<DecodeResult>>(Array.Empty<DecodeResult>());

        return Task.Run(() => dec.Decode(samples, freqLow, freqHigh, utcTime), cancellationToken);
    }

    /// <summary>Synchronous overload — blocks the calling thread until decode completes.</summary>
    public IReadOnlyList<DecodeResult> Decode(
        ReadOnlySpan<float> samples,
        DigitalMode mode,
        double freqLow,
        double freqHigh,
        string utcTime)
    {
        if (!_decoders.TryGetValue(mode, out var dec))
            return Array.Empty<DecodeResult>();

        return dec.Decode(samples, freqLow, freqHigh, utcTime);
    }

    // ── Mode support query ────────────────────────────────────────────────────

    public bool Supports(DigitalMode mode) => _decoders.ContainsKey(mode);

    public IReadOnlyList<DigitalMode> SupportedModes =>
        _decoders.Keys.OrderBy(m => (int)m).ToList();

    // ── Internal registration ─────────────────────────────────────────────────

    private void Register(DigitalMode mode, IDigitalModeDecoder dec)
    {
        dec.Configure(_options);
        dec.ResultAvailable += r => ResultAvailable?.Invoke(r);
        _decoders[mode] = dec;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var dec in _decoders.Values)
            if (dec is IDisposable d) d.Dispose();
    }
}

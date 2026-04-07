using HamDigiSharp.Abstractions;
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
using HamDigiSharp.Decoders.Wspr;
using HamDigiSharp.Encoders;
using HamDigiSharp.Models;

namespace HamDigiSharp.Protocols;

/// <summary>
/// Static registry of all supported ham radio digital mode protocols.
///
/// <code>
///   // Get one protocol
///   IProtocol proto = ProtocolRegistry.Get(DigitalMode.FT8);
///
///   // Browse all
///   foreach (var p in ProtocolRegistry.All.Values)
///       Console.WriteLine($"{p.Name}  period={p.PeriodDuration.TotalSeconds} s");
/// </code>
/// </summary>
public static class ProtocolRegistry
{
    /// <summary>All supported protocols keyed by <see cref="DigitalMode"/>.</summary>
    public static IReadOnlyDictionary<DigitalMode, IProtocol> All { get; } = Build();

    /// <summary>Returns the protocol for <paramref name="mode"/>.</summary>
    /// <exception cref="ArgumentException">If the mode is not registered.</exception>
    public static IProtocol Get(DigitalMode mode) =>
        All.TryGetValue(mode, out var p) ? p
            : throw new ArgumentException($"Unknown mode: {mode}", nameof(mode));

    // ── Builder ───────────────────────────────────────────────────────────────

    private static Dictionary<DigitalMode, IProtocol> Build()
    {
        var d = new Dictionary<DigitalMode, IProtocol>();

        // ── MSK144 ────────────────────────────────────────────────────────────
        d[DigitalMode.MSK144] = P(DigitalMode.MSK144,
            "MSK144",
            "Minimum-shift keying, 144 symbols, 1 s period; high-speed meteor scatter",
            freqLow: 200, freqHigh: 2500,
            constraints: MessageConstraints.Standard(13),
            decoder: () => new Msk144Decoder(),
            encoder: () => new Msk144Encoder());

        // ── JTMS ──────────────────────────────────────────────────────────────
        d[DigitalMode.JTMS] = P(DigitalMode.JTMS,
            "JTMS",
            "JT meteor scatter, 15 s period; short random-access bursts",
            freqLow: 200, freqHigh: 2000,
            constraints: MessageConstraints.Jtms(15),
            decoder: () => new JtmsDecoder(),
            encoder: () => new JtmsEncoder());

        // ── FSK441 ────────────────────────────────────────────────────────────
        d[DigitalMode.FSK441] = P(DigitalMode.FSK441,
            "FSK441",
            "4-FSK at 441 Hz tone spacing, 15 s period; meteor scatter",
            freqLow: 200, freqHigh: 2500,
            constraints: MessageConstraints.Fsk(46),
            decoder: () => new Fsk441Decoder(),
            encoder: () => new Fsk441Encoder());

        // ── FSK315 ────────────────────────────────────────────────────────────
        d[DigitalMode.FSK315] = P(DigitalMode.FSK315,
            "FSK315",
            "4-FSK at 315 Hz tone spacing, 15 s period; meteor scatter",
            freqLow: 200, freqHigh: 2500,
            constraints: MessageConstraints.Fsk(46),
            decoder: () => new Fsk315Decoder(),
            encoder: () => new Fsk315Encoder());

        // ── ISCAT-A ───────────────────────────────────────────────────────────
        d[DigitalMode.IscatA] = P(DigitalMode.IscatA,
            "ISCAT-A",
            "Ionospheric scatter, sub-mode A (~22 baud, 46 ms/symbol), 30 s period",
            freqLow: 200, freqHigh: 2700,
            constraints: MessageConstraints.Iscat(28),
            decoder: () => new IscatDecoder(DigitalMode.IscatA),
            encoder: () => new IscatEncoder(DigitalMode.IscatA));

        // ── ISCAT-B ───────────────────────────────────────────────────────────
        d[DigitalMode.IscatB] = P(DigitalMode.IscatB,
            "ISCAT-B",
            "Ionospheric scatter, sub-mode B (~43 baud, 23 ms/symbol), 30 s period",
            freqLow: 200, freqHigh: 2700,
            constraints: MessageConstraints.Iscat(28),
            decoder: () => new IscatDecoder(DigitalMode.IscatB),
            encoder: () => new IscatEncoder(DigitalMode.IscatB));

        // ── JT6M ──────────────────────────────────────────────────────────────
        d[DigitalMode.JT6M] = P(DigitalMode.JT6M,
            "JT6M",
            "JT 6 m meteor, 60 s period; Reed-Solomon, 65-FSK",
            freqLow: 200, freqHigh: 2700,
            constraints: MessageConstraints.Standard(13),
            decoder: () => new Jt6mDecoder(),
            encoder: () => new Jt6mEncoder());

        // ── JT65A / JT65B / JT65C ─────────────────────────────────────────────
        d[DigitalMode.JT65A] = P(DigitalMode.JT65A,
            "JT65A",
            "JT65 sub-mode A, 60 s period; Reed-Solomon, 65-FSK, 2.69 Hz tone spacing",
            freqLow: 200, freqHigh: 2700,
            constraints: MessageConstraints.Standard(13),
            decoder: () => new Jt65Decoder(DigitalMode.JT65A),
            encoder: () => new Jt65Encoder(DigitalMode.JT65A));

        d[DigitalMode.JT65B] = P(DigitalMode.JT65B,
            "JT65B",
            "JT65 sub-mode B, 60 s period; 65-FSK, 5.38 Hz tone spacing",
            freqLow: 200, freqHigh: 2700,
            constraints: MessageConstraints.Standard(13),
            decoder: () => new Jt65Decoder(DigitalMode.JT65B),
            encoder: () => new Jt65Encoder(DigitalMode.JT65B));

        d[DigitalMode.JT65C] = P(DigitalMode.JT65C,
            "JT65C",
            "JT65 sub-mode C, 60 s period; 65-FSK, 10.77 Hz tone spacing",
            freqLow: 200, freqHigh: 2700,
            constraints: MessageConstraints.Standard(13),
            decoder: () => new Jt65Decoder(DigitalMode.JT65C),
            encoder: () => new Jt65Encoder(DigitalMode.JT65C));

        // ── PI4 ───────────────────────────────────────────────────────────────
        d[DigitalMode.PI4] = P(DigitalMode.PI4,
            "PI4",
            "PI4 beacon, 30 s period; callsign identification on VHF/UHF",
            freqLow: 200, freqHigh: 2000,
            constraints: MessageConstraints.Pi4Callsign(8),
            decoder: () => new Pi4Decoder(),
            encoder: () => new Pi4Encoder());

        // ── FT8 ───────────────────────────────────────────────────────────────
        d[DigitalMode.FT8] = P(DigitalMode.FT8,
            "FT8",
            "Weak-signal 8-FSK, 15 s period, LDPC(174,91); the most widely used DX mode",
            freqLow: 200, freqHigh: 3800,
            constraints: MessageConstraints.Standard(13),
            decoder: () => new Ft8Decoder(),
            encoder: () => new Ft8Encoder());

        // ── MSKMS ─────────────────────────────────────────────────────────────
        d[DigitalMode.MSKMS] = P(DigitalMode.MSKMS,
            "MSKMS",
            "MSK meteor scatter for the 40 m band (alternate sync word), 1 s period",
            freqLow: 200, freqHigh: 2500,
            constraints: MessageConstraints.Standard(13),
            decoder: () => new Msk40Decoder(),
            encoder: () => new Msk40Encoder());

        // ── FT4 ───────────────────────────────────────────────────────────────
        d[DigitalMode.FT4] = P(DigitalMode.FT4,
            "FT4",
            "Weak-signal 4-FSK, 7.5 s period, LDPC(174,91); fast contesting",
            freqLow: 200, freqHigh: 3800,
            constraints: MessageConstraints.Standard(13),
            decoder: () => new Ft4Decoder(),
            encoder: () => new Ft4Encoder());

        // ── Q65A / Q65B / Q65C / Q65D ─────────────────────────────────────────
        d[DigitalMode.Q65A] = P(DigitalMode.Q65A,
            "Q65A",
            "Q65 sub-mode A, 60 s period; 65-FSK LDPC, designed for EME and scatter",
            freqLow: 200, freqHigh: 2700,
            constraints: MessageConstraints.Standard(13),
            decoder: () => new Q65Decoder(DigitalMode.Q65A),
            encoder: () => new Q65Encoder(DigitalMode.Q65A));

        d[DigitalMode.Q65B] = P(DigitalMode.Q65B,
            "Q65B",
            "Q65 sub-mode B, 30 s period; 65-FSK LDPC",
            freqLow: 200, freqHigh: 2700,
            constraints: MessageConstraints.Standard(13),
            decoder: () => new Q65Decoder(DigitalMode.Q65B),
            encoder: () => new Q65Encoder(DigitalMode.Q65B));

        d[DigitalMode.Q65C] = P(DigitalMode.Q65C,
            "Q65C",
            "Q65 sub-mode C, 15 s period; 65-FSK LDPC",
            freqLow: 200, freqHigh: 2700,
            constraints: MessageConstraints.Standard(13),
            decoder: () => new Q65Decoder(DigitalMode.Q65C),
            encoder: () => new Q65Encoder(DigitalMode.Q65C));

        d[DigitalMode.Q65D] = P(DigitalMode.Q65D,
            "Q65D",
            "Q65 sub-mode D, 7.5 s period; 65-FSK LDPC",
            freqLow: 200, freqHigh: 2700,
            constraints: MessageConstraints.Standard(13),
            decoder: () => new Q65Decoder(DigitalMode.Q65D),
            encoder: () => new Q65Encoder(DigitalMode.Q65D));

        // ── FT2 ───────────────────────────────────────────────────────────────
        d[DigitalMode.FT2] = P(DigitalMode.FT2,
            "FT2",
            "Weak-signal 4-FSK, 3.75 s period, LDPC(174,91); fast EME and local scatter (IU8LMC)",
            freqLow: 200, freqHigh: 3800,
            constraints: MessageConstraints.Standard(13),
            decoder: () => new Ft2Decoder(),
            encoder: () => new Ft2Encoder());

        // ── SuperFox ──────────────────────────────────────────────────────────
        d[DigitalMode.SuperFox] = P(DigitalMode.SuperFox,
            "SuperFox",
            "FT8 SuperFox/Hound multi-station DXpedition protocol, 15 s period",
            freqLow: 200, freqHigh: 3800,
            constraints: MessageConstraints.SuperFox(100),
            decoder: () => new SuperFoxDecoder(),
            encoder: () => new SuperFoxEncoder());

        // ── WSPR ──────────────────────────────────────────────────────────────
        d[DigitalMode.Wspr] = P(DigitalMode.Wspr,
            "WSPR",
            "Weak Signal Propagation Reporter, 4-FSK, 120 s period; beacon / propagation mapping",
            freqLow: 200, freqHigh: 3000,
            constraints: MessageConstraints.Wspr(22),
            decoder: () => new WsprDecoder(),
            encoder: () => new WsprEncoder());

        return d;
    }

    // ── Private factory helper ─────────────────────────────────────────────────

    private static Protocol P(
        DigitalMode mode,
        string name,
        string description,
        double freqLow,
        double freqHigh,
        IMessageConstraints constraints,
        Func<IDigitalModeDecoder>?  decoder = null,
        Func<IDigitalModeEncoder>?  encoder = null) =>
        new(
            mode,
            name,
            description,
            periodDuration:     TimeSpan.FromSeconds(mode.PeriodSeconds()),
            transmitDuration:   TimeSpan.FromSeconds(mode.SignalDurationSeconds()),
            sampleRate:         mode.DecoderSampleRate(),
            defaultFreqLow:     freqLow,
            defaultFreqHigh:    freqHigh,
            messageConstraints: constraints,
            decoderFactory:     decoder ?? throw new ArgumentNullException(nameof(decoder)),
            encoderFactory:     encoder);
}

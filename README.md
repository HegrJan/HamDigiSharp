# HamDigiSharp

A pure managed .NET 8 class library for **ham radio digital mode operation** — both decoding
and transmitting. An independent C# implementation of the protocols used by
[MSHV](https://lz2hv.org/mshv) (C++/Qt, by Hrisimir Hristov LZ2HV) and
[WSJT-X](https://wsjt.sourceforge.io/), written from scratch against their reference sources
and verified by round-trip testing.

> Licensed under the **GNU General Public License v3 or later (GPLv3+)** in recognition of the
> upstream protocol specifications. See [LICENSE](LICENSE) and [NOTICE](NOTICE).

---

## Goals

- **Encode + Decode** — full TX and RX sides for all 20 modes; Q65 decoder supports up to 5-period incoherent averaging for EME/weak-signal work
- **Pure managed** — no FFTW3 or other native dependencies; uses [MathNet.Numerics](https://numerics.mathdotnet.com/)
- **All 19 MSHV modes + SuperFox** — full encoder and decoder for every mode (see table below)
- **Extensible** — add new modes by implementing `IDigitalModeDecoder` / `IDigitalModeEncoder`
- **Reusable** — designed for embedding in any .NET GUI application

---

## Projects

| Project | Purpose |
|---------|---------|
| `HamDigiSharp` | Core library — decoders, encoders, DSP, codecs, engine, protocol layer |
| `HamDigiSharp.Tests` | xUnit test suite covering all codecs, DSP, encoder/decoder round-trips and protocol helpers |
| `HamDigiSharp.Demo` | WPF demo application (Windows only) — real-time capture, WAV decode, TX audio |
| `HamDigiSharp.Example` | Console app — load a WAV file, decode, print results |

---

## Quick Start — Protocols (Recommended for GUI apps)

`HamDigiSharp.Protocols` provides a single `IProtocol` object per mode that bundles
period timing, encoder and decoder factories so the GUI never needs to hard-code
mode-specific constants.

```csharp
using HamDigiSharp.Protocols;
using HamDigiSharp.Models;

IProtocol proto = ProtocolRegistry.Get(DigitalMode.FT8);

// ── Timing ────────────────────────────────────────────────────────────────────
// When does the current period start?
DateTimeOffset start = proto.PeriodStart(DateTimeOffset.UtcNow);
DateTimeOffset next  = proto.NextPeriodStart(DateTimeOffset.UtcNow);

// Is the station I just heard transmitting in an even or odd period?
// You always respond in the opposite parity.
bool heardOnEven = proto.IsEvenPeriod(DateTimeOffset.UtcNow);
// → if true: their period is even, so I transmit in the next ODD period

// ── Decode ────────────────────────────────────────────────────────────────────
var decoder = proto.CreateDecoder();
var results = decoder.Decode(samples, proto.DefaultFreqLow, proto.DefaultFreqHigh, "143000");

foreach (var r in results)
    Console.WriteLine(r);  // "143000  -07  0.3   1234  CQ W1AW FN42"

// ── Encode ────────────────────────────────────────────────────────────────────
float[]? audio = proto.CreateEncoder()?.Encode("CQ W1AW FN42", new EncoderOptions
{
    FrequencyHz = 1500.0,
    Amplitude   = 0.5,
});

// ── Browse all protocols ──────────────────────────────────────────────────────
foreach (var p in ProtocolRegistry.All.Values)
    Console.WriteLine($"{p.Name,-10} period={p.PeriodDuration.TotalSeconds} s  " +
                      $"TX={p.TransmitDuration.TotalSeconds:F2} s  SR={p.SampleRate} Hz");
```

---

## Quick Start — Decoding (Low-level)

```csharp
using HamDigiSharp.Engine;
using HamDigiSharp.Models;

// 1. Create engine (all 20 modes registered automatically)
using var engine = new DecoderEngine();

// 2. Configure your station info
engine.Configure(new DecoderOptions
{
    MyCall  = "W1AW",
    HisCall = "K1JT",
    DecoderDepth = DecoderDepth.Fast,
    ApDecode = true,
});

// 3. Subscribe to results
engine.ResultAvailable += r =>
    Console.WriteLine($"{r.UtcTime}  {r.Snr,4:+#;-#;0} dB  {r.FrequencyHz,7:F1} Hz  {r.Message}");

// 4. Feed one period of 12 kHz float PCM to the FT8 decoder
float[] samples = /* load 15-second WAV at 12 kHz */ ...;
var results = engine.Decode(samples, DigitalMode.FT8,
    freqLow: 200, freqHigh: 3000, utcTime: "130000");
```

---

## Quick Start — Encoding (TX)

```csharp
using HamDigiSharp.Engine;
using HamDigiSharp.Models;

using var enc = new EncoderEngine();

// FT8, FT4, FT2, MSK144, JT65A/B/C, JT6M, PI4, Q65A/B/C/D — standard messages
float[] audio = enc.Encode("CQ W1AW FN42", DigitalMode.FT8, new EncoderOptions
{
    FrequencyHz = 1500.0,  // TX audio frequency
    Amplitude   = 0.5,     // peak amplitude (0..1)
});

// SuperFox (Fox → Hound direction) — CQ or compound report message
float[] cqAudio   = enc.Encode("CQ LZ2HVV KN23", DigitalMode.SuperFox);
float[] sfoxAudio = enc.Encode("LZ2HVV W4ABC +01 VK3ABC -03", DigitalMode.SuperFox);
```

### Supported message formats (encoder)

```
CQ [callsign] [grid4]             e.g.  CQ W1AW FN42
[callsign] [callsign] [grid4]           W1AW K1JT FN42
[callsign] [callsign] [report]          W1AW K1JT -07
[callsign] [callsign] R[report]         W1AW K1JT R-07
[callsign] [callsign] RRR / RR73 / 73
```

Callsign rules: standard 3–6 character callsigns (e.g. `W1AW`, `LZ2HV`, `OH2BH/P`).
Grid: 4-character Maidenhead locator (e.g. `FN42`, `KN23`).

---

## Supported Modes

| # | `DigitalMode` | Period | TX duration | Sample rate | Codec | TX |
|---|--------------|--------|-------------|-------------|-------|----|
| 0 | `MSK144` | 1 s | 0.072 s | 12 kHz | LDPC(128,90) + CRC-13 | ✅ |
| 1 | `JTMS` | 15 s | 15 s | 11025 Hz | 2-FSK sliding window | ✅ |
| 2 | `FSK441` | 15 s | burst | 11025 Hz | 4-FSK, 441 Hz spacing | ✅ |
| 3 | `FSK315` | 15 s | burst | 11025 Hz | 4-FSK, 315 Hz spacing | ✅ |
| 4 | `IscatA` | 30 s | 30 s | 11025 Hz | 42-FSK (Costas sync) | ✅ |
| 5 | `IscatB` | 30 s | 30 s | 11025 Hz | 42-FSK (Costas sync) | ✅ |
| 6 | `JT6M` | 60 s | 46.8 s | 11025 Hz | 65-FSK + RS(63,12) | ✅ |
| 7 | `JT65A` | 60 s | 46.8 s | 11025 Hz | 65-FSK + RS(63,12) | ✅ |
| 8 | `JT65B` | 60 s | 46.8 s | 11025 Hz | 65-FSK + RS(63,12) | ✅ |
| 9 | `JT65C` | 60 s | 46.8 s | 11025 Hz | 65-FSK + RS(63,12) | ✅ |
| 10 | `PI4` | 30 s | 13.2 s | 11025 Hz | π/4-QPSK + Fano R=½ K=32 | ✅ |
| 11 | `FT8` | 15 s | 12.64 s | 12 kHz | 8-FSK + LDPC(174,91) | ✅ |
| 12 | `MSKMS` | 1 s | 0.072 s | 12 kHz | LDPC(128,90) + CRC-13 | ✅ |
| 13 | `FT4` | 7.5 s | 5.04 s | 12 kHz | 4-FSK + LDPC(174,91) | ✅ |
| 14 | `Q65A` | 60 s | 49.0 s | 12 kHz | 65-FSK + QRA LDPC/GF(64) | ✅ |
| 15 | `Q65B` | 30 s | 24.5 s | 12 kHz | 65-FSK + QRA LDPC/GF(64) | ✅ |
| 16 | `Q65C` | 15 s | 12.2 s | 12 kHz | 65-FSK + QRA LDPC/GF(64) | ✅ |
| 17 | `Q65D` | 7.5 s | 6.1 s | 12 kHz | 65-FSK + QRA LDPC/GF(64) | ✅ |
| 18 | `FT2` | 3.75 s | 2.52 s | 12 kHz | 4-FSK + LDPC(174,91) | ✅ |
| 19 | `SuperFox` | 15 s | 12.64 s | 12 kHz | FT8 hound + QPC polar code | ✅ |

---

## Building & Testing

```powershell
cd HamDigiSharp
dotnet build
dotnet test HamDigiSharp.Tests
```

Requires **.NET 8+ SDK**.

---

## Real-Time Decoding

```csharp
using HamDigiSharp.Engine;
using HamDigiSharp.Models;

using var engine = new DecoderEngine();
engine.Configure(new DecoderOptions { MyCall = "W1AW", HisCall = "K1JT" });

using var rt = new RealTimeDecoder(engine, DigitalMode.FT8, captureRate: 48000)
{
    FreqLow = 200, FreqHigh = 3000, AlignToUtc = true,
};

rt.PeriodDecoded += (results, windowStart) =>
{
    foreach (var r in results)
        Console.WriteLine($"{windowStart:HH:mm:ss}  {r.Snr,4:+#;-#;0} dB  {r.FrequencyHz,6:F0} Hz  {r.Message}");
};

void OnAudioData(float[] chunk) => rt.AddSamples(chunk);
```

`RealTimeDecoder` resamples from any capture rate to the mode's native 12 000 Hz or 11 025 Hz
automatically.

---

## Author

**Jan Hegr, OK1TE** — <ok1te@email.cz>

Algorithms and protocol specifications copyright © 2001–2025 by the WSJT Development Group and MSHV contributors — see [NOTICE](NOTICE).


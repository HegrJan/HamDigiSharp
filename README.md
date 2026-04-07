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

- **Encode + Decode** — full TX and RX sides for all 20 modes; Q65 supports up to 5-period incoherent averaging for EME/weak-signal work
- **Pure managed** — no FFTW3 or other native dependencies; uses [MathNet.Numerics](https://numerics.mathdotnet.com/)
- **All 19 MSHV modes + SuperFox** — full encoder and decoder for every mode (see table below)
- **Extensible** — add new modes by implementing `IDigitalModeDecoder` / `IDigitalModeEncoder`
- **Reusable** — designed for embedding in any .NET GUI application

---

## Projects

| Project | Purpose |
|---------|---------|
| `HamDigiSharp` | Core library — decoders, encoders, DSP, codecs, engine, protocol and messaging layers |
| `HamDigiSharp.Tests` | xUnit test suite — codecs, DSP, encoder/decoder round-trips, protocol helpers, messaging |
| `HamDigiSharp.Demo` | WPF demo application (Windows only) — real-time capture, WAV decode, TX audio |
| `HamDigiSharp.Example` | Console app — load a WAV file, decode FT8, print results |

---

## API Guides

| Guide | Description |
|-------|-------------|
| [docs/protocols.md](docs/protocols.md) | `ProtocolRegistry`, `IProtocol`, timing helpers |
| [docs/messaging.md](docs/messaging.md) | `MessageBuilder`, `MessageParser`, parsed message types |
| [docs/decoding.md](docs/decoding.md) | `DecoderEngine`, `DecoderOptions`, `DecoderDepth`, Q65 averaging |
| [docs/encoding.md](docs/encoding.md) | `EncoderEngine`, `EncoderOptions`, all message formats |
| [docs/realtime.md](docs/realtime.md) | `RealTimeDecoder`, audio capture, UTC alignment |

---

## Supported Modes

| `DigitalMode` | Period | Sample rate | Codec |
|--------------|--------|-------------|-------|
| `MSK144` | 1 s | 12 kHz | LDPC(128,90) + CRC-13 |
| `JTMS` | 15 s | 11025 Hz | 2-FSK sliding window |
| `FSK441` | 15 s | 11025 Hz | 4-FSK, 441 Hz spacing |
| `FSK315` | 15 s | 11025 Hz | 4-FSK, 315 Hz spacing |
| `IscatA` | 30 s | 11025 Hz | 42-FSK (Costas sync) |
| `IscatB` | 30 s | 11025 Hz | 42-FSK (Costas sync) |
| `JT6M` | 60 s | 11025 Hz | 65-FSK + RS(63,12) |
| `JT65A` | 60 s | 11025 Hz | 65-FSK + RS(63,12) |
| `JT65B` | 60 s | 11025 Hz | 65-FSK + RS(63,12) |
| `JT65C` | 60 s | 11025 Hz | 65-FSK + RS(63,12) |
| `PI4` | 30 s | 11025 Hz | π/4-QPSK + Fano R=½ K=32 |
| `FT8` | 15 s | 12 kHz | 8-FSK + LDPC(174,91) |
| `MSKMS` | 1 s | 12 kHz | LDPC(128,90) + CRC-13 |
| `FT4` | 7.5 s | 12 kHz | 4-FSK + LDPC(174,91) |
| `Q65A` | 60 s | 12 kHz | 65-FSK + QRA LDPC/GF(64) |
| `Q65B` | 30 s | 12 kHz | 65-FSK + QRA LDPC/GF(64) |
| `Q65C` | 15 s | 12 kHz | 65-FSK + QRA LDPC/GF(64) |
| `Q65D` | 7.5 s | 12 kHz | 65-FSK + QRA LDPC/GF(64) |
| `FT2` | 3.75 s | 12 kHz | 4-FSK + LDPC(174,91) |
| `SuperFox` | 15 s | 12 kHz | FT8 hound + QPC polar code |
| `Wspr` | 120 s | 12 kHz | 4-FSK + Fano R=½ K=32 (wsprd-compatible) |

All 21 modes support both encode and decode.

---

## Building & Testing

```powershell
cd HamDigiSharp
dotnet build
dotnet test HamDigiSharp.Tests
```

Requires **.NET 8+ SDK**.

---

## Author

**Jan Hegr, OK1TE** — <ok1te@email.cz>

Algorithms and protocol specifications copyright © 2001–2025 by the WSJT Development Group and MSHV contributors — see [NOTICE](NOTICE).


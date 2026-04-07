# HamDigiSharp — Decoding

Two decoding APIs are available:

| API | Use when |
|---|---|
| `ProtocolRegistry` + `IProtocol` | Protocol-first: you know the mode and want timing + decode in one object |
| `DecoderEngine` | You manage timing yourself; feed raw samples to a mode decoder |

---

## Via IProtocol (recommended)

```csharp
using HamDigiSharp.Protocols;
using HamDigiSharp.Models;

IProtocol proto = ProtocolRegistry.Get(DigitalMode.FT8);
var decoder = proto.CreateDecoder();

// samples: float[], 15 s at 12 000 Hz, peak amplitude ±1
var results = decoder.Decode(samples, proto.DefaultFreqLow, proto.DefaultFreqHigh, "143000");

foreach (var r in results)
    Console.WriteLine(r);  // "143000  -07  0.3   1234  CQ W1AW FN42"
```

---

## Via DecoderEngine

`DecoderEngine` manages all mode decoders and forwards `ResultAvailable` events.

```csharp
using HamDigiSharp.Engine;
using HamDigiSharp.Models;

using var engine = new DecoderEngine();

engine.Configure(new DecoderOptions
{
    MyCall       = "W1AW",           // enables AP (a-priori) decoding passes
    HisCall      = "K1JT",           // callsign of the station you are working
    DecoderDepth = DecoderDepth.Normal,
    ApDecode     = true,
    MaxCandidates = 500,
    MinSyncDb    = 2.5f,
    QsoProgress  = QsoProgress.None,
});

engine.ResultAvailable += r =>
    Console.WriteLine($"{r.UtcTime}  {r.Snr,4:+#;-#;0} dB  {r.FrequencyHz,7:F1} Hz  {r.Message}");

// Decode one 15-second FT8 period (12 000 Hz float PCM)
var results = engine.Decode(samples, DigitalMode.FT8, freqLow: 200, freqHigh: 3000, utcTime: "130000");
```

---

## DecoderOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `MyCall` | `string?` | `null` | Your callsign — enables AP decode passes |
| `HisCall` | `string?` | `null` | Callsign you are working |
| `DecoderDepth` | `DecoderDepth` | `Normal` | LDPC OSD depth (see below) |
| `ApDecode` | `bool` | `false` | Run additional a-priori passes |
| `MaxCandidates` | `int` | `500` | Sync candidates per period |
| `MinSyncDb` | `float` | `2.5` | Minimum sync score to attempt decode |
| `QsoProgress` | `QsoProgress` | `None` | AP bias toward expected exchange tokens |

### DecoderDepth

| Value | OSD passes | Use case |
|---|---|---|
| `Fast` | None (BP only) | Real-time, low CPU |
| `Normal` | BP + OSD-1 | Balanced (default) |
| `Deep` | BP + OSD-2 | Maximum sensitivity |

### QsoProgress

Used to bias AP decoding toward the message type you expect next in the QSO flow:

```
None → Called → ReportReceived → RrrReceived → Completed
```

---

## DecodeResult fields

| Property | Type | Description |
|---|---|---|
| `UtcTime` | `string` | Six-digit UTC stamp (`HHMMSS`) or period label |
| `Snr` | `float` | Signal-to-noise ratio in dB |
| `DeltaTime` | `float` | Time offset relative to nominal period start (s) |
| `FrequencyHz` | `double` | Carrier/sync tone frequency in Hz |
| `Message` | `string` | Decoded text |

---

## Decoding from a WAV file

See `HamDigiSharp.Example/Program.cs` for a complete working example that:
- Loads any WAV file (auto-resampled)
- Splits into mode-length periods
- Decodes all periods and prints results

---

## Q65 multi-period averaging

Q65 improves sensitivity for very weak signals (EME, tropo) by coherently averaging
up to 5 successive periods:

```csharp
// Pass the same DecoderOptions instance for consecutive periods.
// The Q65 decoder accumulates LLRs internally and re-tries LDPC after each period.
var opts = new DecoderOptions { DecoderDepth = DecoderDepth.Deep };
var q65decoder = ProtocolRegistry.Get(DigitalMode.Q65A).CreateDecoder();
q65decoder.Configure(opts);

for (int i = 0; i < 5; i++)
{
    var r = q65decoder.Decode(GetNextPeriod(), 200, 3000, $"period{i}");
    if (r.Count > 0) break;  // decoded — no need for more averages
}
```

---

## WSPR multi-pass decode

WSPR uses a two-pass pipeline with signal subtraction to recover signals that would
be masked by stronger co-channel signals:

```
Pass 1 (Fano-only)
 ├─ decode all candidates with Fano
 ├─ add callsigns to AP hash
 └─ subtract decoded signals from the complex baseband

Pass 2 (Fano + OSD, AP-gated)
 ├─ re-detect candidates from cleaned baseband
 ├─ try Fano first
 ├─ if Fano fails → OSD fallback (depth=1)
 └─ OSD result only accepted if callsign is in AP hash (prevents phantoms)
```

### AP hash lifetime

The AP hash persists across `Decode()` calls on the **same decoder instance**,
accumulating known callsigns over the receive session and improving sensitivity
for stations seen in earlier periods.

```csharp
var decoder = new WsprDecoder();

// Period 1 — strong station W1AW decoded; hash now contains "W1AW"
var r1 = decoder.Decode(period1, 1400, 1600, "000000");

// Period 2 — W1AW is weaker; OSD accepts it because hash contains "W1AW"
var r2 = decoder.Decode(period2, 1400, 1600, "000200");

// Start fresh (e.g., new band or new session)
decoder.Reset();
```

### OSD depth

`WsprConv.OsdDecode(softSymbols, depth: 1, out decoded)`:

| `depth` | Candidates | Pre-screen | Approx. time |
|---------|-----------|------------|--------------|
| `1` | K+1 = 51 | ntheta=16 | &lt;5 ms |
| `2` | ~1276 | ntheta=22 | ~50 ms |

The decoder uses `depth=1` by default (real-time safe).


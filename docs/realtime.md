# HamDigiSharp — Real-Time Decoding

`RealTimeDecoder` wraps a `DecoderEngine` and handles all the plumbing for
continuous audio capture: it accumulates raw samples, aligns period boundaries to
the UTC clock, resamples if needed, and fires `PeriodDecoded` after each period.

---

## Basic setup

```csharp
using HamDigiSharp.Engine;
using HamDigiSharp.Models;

var engine = new DecoderEngine();
engine.Configure(new DecoderOptions
{
    MyCall       = "W1AW",
    DecoderDepth = DecoderDepth.Normal,
    ApDecode     = true,
});

using var rt = new RealTimeDecoder(engine, DigitalMode.FT8, captureRate: 48000)
{
    FreqLow    = 200,
    FreqHigh   = 3000,
    AlignToUtc = true,   // pad first window to the next UTC period boundary
};

rt.PeriodDecoded += (results, windowStart) =>
{
    foreach (var r in results)
        Console.WriteLine($"{windowStart:HH:mm:ss}  {r.Snr,4:+#;-#;0} dB  {r.FrequencyHz,6:F0} Hz  {r.Message}");
};

// Feed audio from your capture callback
void OnAudioData(float[] chunk) => rt.AddSamples(chunk);
```

---

## Resampling

`RealTimeDecoder` accepts any `captureRate` and resamples internally to the mode's
native rate (12 000 Hz or 11 025 Hz). Common capture rates 8 000–96 000 Hz are all
supported via the `Resampler` (polyphase FIR).

---

## Changing modes at runtime

```csharp
rt.ChangeMode(DigitalMode.JT65A, captureRate: 48000);
// Clears internal buffers; next AddSamples call starts a fresh period
```

---

## Transmission timing

When `AlignToUtc = true`, `RealTimeDecoder` waits until the next UTC period boundary
before it begins accumulating the first decode window.  Subsequent periods follow
immediately with no gaps.

`NextPeriodStart(DateTimeOffset.UtcNow)` on the protocol object gives the exact UTC
`DateTimeOffset` when the next TX window opens — useful for scheduling audio playback:

```csharp
IProtocol proto = ProtocolRegistry.Get(DigitalMode.FT8);
DateTimeOffset txAt = proto.NextPeriodStart(DateTimeOffset.UtcNow);

// Schedule audio output to start exactly at txAt
scheduler.PlayAt(audio, txAt);
```

---

## RealTimeDecoder properties

| Property | Type | Default | Description |
|---|---|---|---|
| `FreqLow` | `double` | `200` | Lower edge of decode window (Hz) |
| `FreqHigh` | `double` | `3000` | Upper edge of decode window (Hz) |
| `AlignToUtc` | `bool` | `true` | Align first window to UTC period boundary |

---

## Disposal

`RealTimeDecoder` is `IDisposable`. The underlying `DecoderEngine` is **not** disposed
when `RealTimeDecoder` is disposed — you own and dispose it separately:

```csharp
using var engine = new DecoderEngine();
using var rt     = new RealTimeDecoder(engine, DigitalMode.FT8, captureRate: 48000);
// …
// rt is disposed first, engine second (both at end of using block or explicit Dispose)
```

# HamDigiSharp — Protocol Layer

`HamDigiSharp.Protocols` provides one `IProtocol` object per mode.
It bundles timing constants, encoder/decoder factories, and message constraints so
a GUI application never has to hard-code mode-specific values.

---

## Getting a protocol

```csharp
using HamDigiSharp.Protocols;
using HamDigiSharp.Models;

IProtocol proto = ProtocolRegistry.Get(DigitalMode.FT8);
```

All 20 modes are registered. Use `ProtocolRegistry.All` for the complete dictionary
keyed by `DigitalMode`.

---

## Timing

```csharp
// When did the current period start (aligned to UTC clock)?
DateTimeOffset start = proto.PeriodStart(DateTimeOffset.UtcNow);

// When will the next period start?
DateTimeOffset next = proto.NextPeriodStart(DateTimeOffset.UtcNow);

// Was a transmission you just heard on an even (0, 2, 4 …) or odd (1, 3, 5 …) period?
// In a standard QSO you always respond on the opposite parity.
bool heardOnEven = proto.IsEvenPeriod(DateTimeOffset.UtcNow);

// Scalar durations
double period  = proto.PeriodDuration.TotalSeconds;  // e.g. 15.0 for FT8
double txSec   = proto.TransmitDuration.TotalSeconds; // e.g. 12.64 for FT8
int    rate    = proto.SampleRate;                    // 12000 or 11025
```

---

## Decoding

```csharp
var decoder = proto.CreateDecoder();
var results = decoder.Decode(samples, proto.DefaultFreqLow, proto.DefaultFreqHigh, "143000");

foreach (var r in results)
    Console.WriteLine(r);  // "143000  -07  0.3   1234  CQ W1AW FN42"
```

---

## Encoding

```csharp
// CreateEncoder() returns null for receive-only modes
IDigitalModeEncoder? enc = proto.CreateEncoder();
if (enc is not null)
{
    float[] audio = enc.Encode("CQ W1AW FN42", new EncoderOptions
    {
        FrequencyHz = 1500.0,
        Amplitude   = 0.5,
    });
}

// CanEncode is a quick capability check without allocating an encoder
bool canTx = proto.CanEncode;
```

---

## Message constraints

Each protocol exposes `IMessageConstraints` so a UI can validate user input before
attempting to encode:

```csharp
IMessageConstraints c = proto.MessageConstraints;

Console.WriteLine(c.MaxLength);       // max character count
Console.WriteLine(c.AllowedChars);    // set of valid characters
Console.WriteLine(c.FormatHint);      // human-readable description

string? error = c.Validate("CQ W1AW FN42");
if (error is not null)
    ShowError(error);
```

---

## Browsing all protocols

```csharp
foreach (var p in ProtocolRegistry.All.Values.OrderBy(p => p.PeriodDuration))
{
    Console.WriteLine(
        $"{p.Name,-10}  period={p.PeriodDuration.TotalSeconds,5} s  " +
        $"TX={p.TransmitDuration.TotalSeconds:F2} s  SR={p.SampleRate} Hz  " +
        $"TX={p.CanEncode}");
}
```

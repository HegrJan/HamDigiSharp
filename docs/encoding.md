# HamDigiSharp — Encoding (TX)

---

## Via IProtocol (recommended)

```csharp
using HamDigiSharp.Protocols;
using HamDigiSharp.Models;

IProtocol proto = ProtocolRegistry.Get(DigitalMode.FT8);

// Returns null for receive-only modes
IDigitalModeEncoder? enc = proto.CreateEncoder();
float[]? audio = enc?.Encode("CQ W1AW FN42", new EncoderOptions
{
    FrequencyHz = 1500.0,
    Amplitude   = 0.5,
});
```

Use `proto.CanEncode` to check capability without allocating an encoder.

---

## Via EncoderEngine

```csharp
using HamDigiSharp.Engine;
using HamDigiSharp.Models;

using var enc = new EncoderEngine();
float[] audio = enc.Encode("CQ W1AW FN42", DigitalMode.FT8, new EncoderOptions
{
    FrequencyHz = 1500.0,
    Amplitude   = 0.5,
});
```

---

## EncoderOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `FrequencyHz` | `double` | `1500.0` | Audio carrier / lowest tone frequency (Hz) |
| `Amplitude` | `double` | `1.0` | Peak amplitude, clamped to 0 … 1 |
| `SuperFoxSignature` | `uint` | `0` | 20-bit OTP digital signature for SuperFox transmissions (0 = no signature) |

---

## Supported message formats

All standard modes (FT8, FT4, FT2, MSK144, MSKMS, JT65A/B/C, JT6M, PI4, Q65A/B/C/D) share
the same 77-bit message packing and accept the following text formats:

```
CQ [qualifier] callsign grid4        e.g.  CQ W1AW FN42
                                           CQ DX W1AW FN42
                                           CQ 145 W1AW FN42

callsign callsign grid4              e.g.  K9AN W1AW FN42
callsign callsign report             e.g.  K9AN W1AW -07
callsign callsign R report           e.g.  K9AN W1AW R-07
callsign callsign RRR
callsign callsign RR73
callsign callsign 73

QRZ callsign grid4                   e.g.  QRZ W1AW FN42
DE  callsign grid4                   e.g.  DE W1AW FN42
```

**Callsign** — standard 1–2 letter prefix, area digit, 1–3 letter suffix (e.g. `W1AW`,
`LZ2HV`). Compound forms with `/` are accepted: `OH2BH/P`, `DL/W1AW`.

**Grid** — 4-character Maidenhead locator, letters A–R then digits (e.g. `FN42`, `KN23`).

**Report** — signed integer −50 … +49, formatted as `+07` or `-12`.

### WSPR

WSPR uses a compact 50-bit message: callsign, 4-char Maidenhead grid, and power in dBm.

```
callsign grid4 power                 e.g.  W1AW FN42 33
                                           K9AN EN52 37
```

**Callsign** — standard callsign up to 6 characters (including `/P` compound forms).  
**Grid** — 4-character Maidenhead locator (e.g. `FN42`).  
**Power** — transmit power in dBm, rounded to the nearest legal value from:
`{0, 3, 7, 10, 13, 17, 20, 23, 27, 30, 33, 37, 40, 43, 47, 50, 53, 57, 60}` (0 to 60 dBm, ITU steps).

The encoder produces 162 symbols × 4-FSK at 12 kHz, 120-second period.  
The decoder is a port of the wsprd pipeline (K1JT/K9AN, GPL) and is fully compatible
with WSJT-X and other wsprd-based decoders.

### ISCAT-A/B, FSK441, FSK315, JTMS — free text

```
Any text up to 13 characters from: A-Z 0-9 / . space + - ( ) ? =
```

### PI4 — beacon

```
callsign grid4                       e.g.  GB3NGI KN34
```

### SuperFox

```
CQ callsign grid4                    e.g.  CQ LZ2HVV KN23
CQ callsign grid4 ~ free text        e.g.  CQ LZ2HVV KN23 ~ EXPEDITION  (i3=3 + text)

callsign hound1 report1 [hound2 report2 …]
                                     e.g.  LZ2HVV W4ABC -03 VK3ABC RR73 K9AN +07
callsign hound1 [report1] … ~ text   e.g.  LZ2HVV W4ABC -03 G4XYZ ~ QSL QRZ  (i3=2)
```

Up to 9 hounds per i3=0 frame (5 × RR73, 4 with reports). Reports: −18 … +12 dB.  
The `~` separator selects the i3=2 free-text format: up to 4 hounds and 26 characters of
base-42 free text (`space 0-9 A-Z + - . / ?`) in the same transmission.

#### Digital signature (OTP)

Pass a 20-bit one-time-pad code via `EncoderOptions.SuperFoxSignature` to sign transmissions.
The decoder emits `"$VERIFY$ FOXCALL NNNNNN"` when a non-zero signature is decoded; the
application compares the code against its own key for the current UTC period.

```csharp
enc.Encode("CQ LZ2HVV KN23", new EncoderOptions
{
    FrequencyHz      = 750.0,
    SuperFoxSignature = 123456u,  // 20-bit OTP for this period
});
```

---

## Using MessageBuilder to compose messages

`MessageBuilder` validates all fields before returning the ready-to-encode string,
so encoder errors are caught at construction time:

```csharp
using HamDigiSharp.Messaging;

string text = MessageBuilder.Exchange("K9AN", "W1AW", -12).Unwrap();
// "K9AN W1AW -12"  — throws if any field is invalid

var result = MessageBuilder.SuperFoxResponse("LZ2HVV", hounds);
if (result.IsValid)
    enc.Encode(result.Message!, options);
```

See [`messaging.md`](messaging.md) for the full MessageBuilder/MessageParser reference.

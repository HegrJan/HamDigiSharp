# HamDigiSharp — Messaging Layer

`HamDigiSharp.Messaging` sits on top of the encoder/decoder engines and handles the
text layer of a QSO: constructing valid message strings for the encoder, and
parsing decoded strings back into strongly-typed fields for the GUI.

---

## MessageBuilder

All builder methods are static and return `BuildResult`.
Inputs are normalised to upper-case automatically.

### CQ call

```csharp
// "CQ W1AW FN42"
BuildResult result = MessageBuilder.Cq("W1AW", "FN42");

// With geographic qualifier: "CQ EU W1AW FN42"
BuildResult result = MessageBuilder.Cq("W1AW", "FN42", "EU");

// With frequency qualifier: "CQ 145 W1AW FN42"
BuildResult result = MessageBuilder.Cq("W1AW", "FN42", "145");
```

Valid qualifiers: 1–4 letters (`DX`, `EU`, `AF` …) or 1–3 digits (`145`, `432` …).

### Exchange

```csharp
// With SNR report:  "K9AN W1AW -12"
BuildResult result = MessageBuilder.Exchange("K9AN", "W1AW", -12);

// With grid:        "K9AN W1AW FN42"
BuildResult result = MessageBuilder.Exchange("K9AN", "W1AW", "FN42");

// Standard tokens:  "K9AN W1AW RR73"  /  "K9AN W1AW 73"
BuildResult result = MessageBuilder.Exchange("K9AN", "W1AW", "RR73");
```

SNR range: −50 to +49 dB.

### Free text (ISCAT-A/B, JTMS, FSK441, FSK315)

```csharp
BuildResult result = MessageBuilder.FreeText("HELLO DE W1AW", DigitalMode.IscatA);
```

Character set and length are validated against the protocol's `MessageConstraints`.

### PI4 beacon

```csharp
BuildResult result = MessageBuilder.Beacon("GB3NGI", "KN34");
// → "GB3NGI KN34"
```

### SuperFox

```csharp
// Fox CQ:
BuildResult cq = MessageBuilder.SuperFoxCq("LZ2HVV", "KN23");
// → "CQ LZ2HVV KN23"

// Fox compound response (up to 9 hounds: max 5 × RR73, max 4 with reports):
BuildResult response = MessageBuilder.SuperFoxResponse("LZ2HVV", new[]
{
    new HoundEntry { Callsign = "W4ABC",  ReportDb = -3  },
    new HoundEntry { Callsign = "VK3ABC", IsRr73   = true },
    new HoundEntry { Callsign = "K9AN",   ReportDb = +7  },
});
// → "LZ2HVV W4ABC -03 VK3ABC RR73 K9AN +07"
```

`ReportDb` range for SuperFox: −18 to +12 dB.

### Generic validation

```csharp
// Validate any free-form string against a mode's constraints:
BuildResult result = MessageBuilder.Validate("CQ W1AW FN42", DigitalMode.FT8);
```

---

## BuildResult

```csharp
BuildResult result = MessageBuilder.Cq("W1AW", "FN42");

if (!result.IsValid)
{
    // result.Error contains a human-readable description of the first failure
    ShowError(result.Error!);
}
else
{
    // result.Message is the ready-to-encode string
    string text = result.Message!;
    proto.CreateEncoder()?.Encode(text, options);
}

// Alternatively, throw InvalidOperationException on failure:
string msg = result.Unwrap();
```

---

## MessageParser

```csharp
ParsedMessage parsed = MessageParser.Parse(raw, DigitalMode.FT8);
```

The `DigitalMode` argument is optional; when omitted the parser uses heuristics.

### Parsed message types

| Base class | Concrete type | When produced |
|---|---|---|
| `ParsedMessage` | `StandardMessage` | Three-field exchange, CQ, R-reports |
| `ParsedMessage` | `FreeTextMessage` | ISCAT-A/B, FSK441, FSK315, JTMS |
| `ParsedMessage` | `BeaconMessage` | PI4 (`"CALL GRID"` format) |
| `ParsedMessage` | `SuperFoxCqMessage` | `"CQ FOX GRID"` in SuperFox mode |
| `ParsedMessage` | `SuperFoxResponseMessage` | Fox compound frame in SuperFox mode |

### StandardMessage properties

```csharp
if (parsed is StandardMessage sm)
{
    Console.WriteLine(sm.Direction);   // MessageDirection.CQ / Exchange / …
    Console.WriteLine(sm.From);        // transmitting callsign
    Console.WriteLine(sm.To);          // addressed callsign (null for CQ)
    Console.WriteLine(sm.Exchange);    // raw exchange token: grid, report, RR73, …

    int?  snr     = sm.SnrDb;          // non-null when exchange is a report
    bool  hasGrid = sm.HasGrid;        // true when exchange is a Maidenhead grid
}
```

### SuperFoxResponseMessage properties

```csharp
if (parsed is SuperFoxResponseMessage sf)
{
    Console.WriteLine(sf.FoxCallsign);

    foreach (var h in sf.Hounds)
    {
        Console.Write(h.Callsign);
        if (h.IsRr73)          Console.WriteLine(" → RR73");
        else if (h.ReportDb.HasValue) Console.WriteLine($" → {h.ReportDb} dB");
    }
}
```

### SuperFox message duality

The SuperFox fox station *transmits* compound frames — one transmission contains
reports for several hounds at once.  The *decoder* emits one line per hound using
the standard three-field layout (`HOUND FOX REPORT`).

| Direction | Format | Parsed as |
|---|---|---|
| Build (TX) | `LZ2HVV W4ABC -03 VK3ABC RR73` | `SuperFoxResponseMessage` |
| Parse (RX) | `VK3ABC LZ2HVV RR73` | `StandardMessage` (individual hound line) |

---

## Callsign rules

The builder and parser enforce a consistent set of callsign rules derived from
`MessagePack77.Pack28`:

- Area digit at position 1 (1-letter prefix: `W1AW`) or position 2 (2-letter prefix: `LZ2HV`)
- Compound callsigns with `/` are accepted: `OH2BH/P`, `DL/W1AW`
- Callsigns starting with a digit (e.g. `3Y0X`) are **not** supported by standard
  Pack28 and are rejected — they require compound-call encoding
- Maximum 11 characters in base-38 alphabet

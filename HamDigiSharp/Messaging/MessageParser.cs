using HamDigiSharp.Models;

namespace HamDigiSharp.Messaging;

/// <summary>
/// Parses a raw decoded message string into a typed <see cref="ParsedMessage"/>.
/// The optional <paramref name="mode"/> hint enables protocol-specific interpretation
/// (e.g. PI4 always yields <see cref="BeaconMessage"/>; ISCAT always <see cref="FreeTextMessage"/>).
/// Without a mode hint the parser falls back to heuristic pattern matching.
/// </summary>
public static class MessageParser
{
    // Reserved words that are never callsigns
    private static readonly HashSet<string> Reserved =
        new(StringComparer.Ordinal) { "CQ", "QRZ", "DE", "RRR", "RR73", "73", "R" };

    // Free-text-only modes — no structured callsign/grid packing
    private static readonly HashSet<DigitalMode> FreeTextModes = new()
    {
        DigitalMode.IscatA, DigitalMode.IscatB,
        DigitalMode.FSK441, DigitalMode.FSK315,
        DigitalMode.JTMS,
    };

    /// <summary>
    /// Parse <paramref name="message"/> into a structured <see cref="ParsedMessage"/>.
    /// </summary>
    /// <param name="message">Raw decoded message string (e.g. from <c>DecodeResult.Message</c>).</param>
    /// <param name="mode">
    /// Optional digital mode hint. When <see langword="null"/> the parser uses heuristic matching.
    /// </param>
    public static ParsedMessage Parse(string message, DigitalMode? mode = null)
    {
        string raw = message ?? "";
        string msg = raw.Trim().ToUpperInvariant();

        // PI4: callsign beacon only
        if (mode == DigitalMode.PI4)
            return new BeaconMessage { Raw = raw, Callsign = msg };

        // Free-text-only modes
        if (mode.HasValue && FreeTextModes.Contains(mode.Value))
            return new FreeTextMessage { Raw = raw, Text = msg };

        string[] words = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return new FreeTextMessage { Raw = raw, Text = msg };

        // SuperFox has two distinct message layouts
        if (mode == DigitalMode.SuperFox)
            return ParseSuperFox(raw, words);

        return ParseStandard(raw, words);
    }

    // ── SuperFox ─────────────────────────────────────────────────────────────

    private static ParsedMessage ParseSuperFox(string raw, string[] words)
    {
        // "$VERIFY$ FOXCALL SIGCODE" — digital-signature token from SuperFox decoder
        if (words[0] == "$VERIFY$" && words.Length >= 3
            && uint.TryParse(words[2], out uint sig))
            return new SuperFoxSignatureMessage { Raw = raw, FoxCallsign = words[1], SignatureCode = sig };

        // "CQ FOXCALL GRID4"
        if (words[0] == "CQ" && words.Length >= 3)
            return new SuperFoxCqMessage { Raw = raw, FoxCallsign = words[1], Grid = words[2] };

        // Simple decoded Fox→Hound line: "HOUND FOXCALL RR73/REPORT" (3 words, exchange token last)
        if (words.Length == 3 && IsExchangeToken(words[2]))
            return new StandardMessage
            {
                Raw       = raw,
                Direction = MessageDirection.Exchange,
                From      = words[0],
                To        = words[1],
                Exchange  = words[2],
            };

        // Compound encoder format: "FOXCALL H1 [±NN] H2 [±NN] …" (≥2 callsigns after fox)
        int houndCallCount = 0;
        for (int i = 1; i < words.Length; i++)
            if (IsCallsignLike(words[i])) houndCallCount++;

        if (words.Length >= 2 && houndCallCount >= 2)
            return new SuperFoxResponseMessage
            {
                Raw         = raw,
                FoxCallsign = words[0],
                Hounds      = ParseHoundList(words, 1),
            };

        // Two-word simple exchange "HOUND FOXCALL" or compound with one hound
        if (words.Length >= 2)
            return new SuperFoxResponseMessage
            {
                Raw         = raw,
                FoxCallsign = words[0],
                Hounds      = ParseHoundList(words, 1),
            };

        return new FreeTextMessage { Raw = raw, Text = string.Join(" ", words) };
    }

    // ── Standard modes ────────────────────────────────────────────────────────

    private static ParsedMessage ParseStandard(string raw, string[] words)
    {
        string first = words[0];

        // CQ / QRZ / DE — initiator role
        if (first is "CQ" or "QRZ" or "DE")
        {
            var direction = first switch
            {
                "CQ"  => MessageDirection.CQ,
                "QRZ" => MessageDirection.QRZ,
                _     => MessageDirection.DE,
            };

            if (words.Length < 2)
                return new FreeTextMessage { Raw = raw, Text = string.Join(" ", words) };

            // Check for CQ qualifier: a non-callsign second word (e.g. "DX", "EU", "145")
            string? qualifier = null;
            int fromIdx = 1;
            if (direction == MessageDirection.CQ && words.Length >= 3 && !IsCallsignLike(words[1]))
            {
                qualifier = words[1];
                fromIdx   = 2;
            }

            if (fromIdx >= words.Length)
                return new FreeTextMessage { Raw = raw, Text = string.Join(" ", words) };

            string from = words[fromIdx];

            // If it looks like free-text (e.g. "CQ TEST"), fall through
            if (!IsCallsignLike(from) && direction == MessageDirection.CQ)
                return new FreeTextMessage { Raw = raw, Text = string.Join(" ", words) };

            string? exchange = fromIdx + 1 < words.Length ? words[fromIdx + 1] : null;

            return new StandardMessage
            {
                Raw          = raw,
                Direction    = direction,
                CqQualifier  = qualifier,
                From         = from,
                To           = null,
                Exchange     = exchange,
            };
        }

        // Point-to-point exchange: CALL1 CALL2 TOKEN
        if (words.Length >= 3 && IsCallsignLike(words[0]) && IsCallsignLike(words[1]))
        {
            return new StandardMessage
            {
                Raw       = raw,
                Direction = MessageDirection.Exchange,
                From      = words[0],
                To        = words[1],
                Exchange  = words[2],
            };
        }

        // Bare two-callsign pair (unusual but valid in some modes)
        if (words.Length == 2 && IsCallsignLike(words[0]) && IsCallsignLike(words[1]))
        {
            return new StandardMessage
            {
                Raw       = raw,
                Direction = MessageDirection.Exchange,
                From      = words[0],
                To        = words[1],
                Exchange  = null,
            };
        }

        return new FreeTextMessage { Raw = raw, Text = string.Join(" ", words) };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Heuristic check: does <paramref name="s"/> look like an amateur callsign?
    /// Accepts standard callsigns (area digit at position 1 or 2), compound callsigns
    /// with one slash (PREFIX/CALL or CALL/SUFFIX), and avoids false-positive matches
    /// on grid locators and reserved words.
    /// </summary>
    internal static bool IsCallsignLike(string s)
    {
        if (s.Length < 2 || s.Length > 11) return false;
        if (s[0] == '+' || s[0] == '-') return false;
        if (Reserved.Contains(s)) return false;

        // Allow one slash for compound callsigns; but only one
        int slashCount = 0;
        foreach (char c in s)
        {
            if (c == '/') { slashCount++; if (slashCount > 1) return false; }
            else if (!char.IsLetterOrDigit(c)) return false;
        }

        bool hasLetter = false, hasDigit = false;
        foreach (char c in s.Replace("/", ""))
        {
            if (char.IsLetter(c)) hasLetter = true;
            else if (char.IsDigit(c)) hasDigit = true;
        }
        if (!hasLetter || !hasDigit) return false;

        // Reject 4-char patterns that look like Maidenhead grids: [A-R][A-R][0-9]{2}
        if (s.Length == 4)
        {
            char c0 = s[0], c1 = s[1];
            if (c0 >= 'A' && c0 <= 'R' && c1 >= 'A' && c1 <= 'R'
                && char.IsDigit(s[2]) && char.IsDigit(s[3]))
                return false;
        }

        // For compound calls, validate at least one part looks like a real callsign
        if (slashCount == 1)
        {
            int slash = s.IndexOf('/');
            return HasCallsignStructure(s[..slash]) || HasCallsignStructure(s[(slash + 1)..]);
        }

        return HasCallsignStructure(s);
    }

    /// <summary>
    /// Checks that a simple (no-slash) token has the structure of a standard callsign:
    /// at least one digit, and the first digit (area code) is at position 1 or 2.
    /// </summary>
    private static bool HasCallsignStructure(string s)
    {
        if (s.Length < 2) return false;
        for (int i = 0; i < Math.Min(s.Length, 4); i++)
        {
            if (!char.IsDigit(s[i])) continue;
            return i == 1 || i == 2; // area digit must be at position 1 or 2
        }
        return false; // no digit found in first 4 chars
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="s"/> is a recognised exchange token:
    /// a Maidenhead grid, a signed SNR report, or one of RRR / RR73 / 73.
    /// </summary>
    internal static bool IsExchangeToken(string s)
    {
        if (s is "RRR" or "RR73" or "73") return true;
        if (TryParseReport(s, out _)) return true;

        // Grid4: [A-R][A-R][0-9]{2}
        if (s.Length == 4)
        {
            char c0 = s[0], c1 = s[1];
            return c0 >= 'A' && c0 <= 'R' && c1 >= 'A' && c1 <= 'R'
                && char.IsDigit(s[2]) && char.IsDigit(s[3]);
        }

        return false;
    }

    /// <summary>Tries to parse <paramref name="s"/> as a signed integer SNR report (e.g. "+07", "-12").</summary>
    internal static bool TryParseReport(string s, out int value) =>
        int.TryParse(s, out value)
        && (s.StartsWith('+') || s.StartsWith('-') || (s.Length <= 3 && value <= 12));

    /// <summary>
    /// Parses a list of hound callsigns (and optional reports) starting at
    /// <paramref name="startIdx"/> within <paramref name="words"/>.
    /// </summary>
    internal static IReadOnlyList<HoundEntry> ParseHoundList(string[] words, int startIdx)
    {
        var hounds = new List<HoundEntry>();
        HoundEntry? current = null;

        for (int i = startIdx; i < words.Length; i++)
        {
            string w = words[i];

            if (IsCallsignLike(w))
            {
                if (current != null) hounds.Add(current);
                current = new HoundEntry { Callsign = w };
            }
            else if (current != null && TryParseReport(w, out int rpt))
            {
                hounds.Add(current with { ReportDb = rpt });
                current = null;
            }
            else if (current != null && w == "RR73")
            {
                hounds.Add(current); // no report = RR73
                current = null;
            }
            // Otherwise skip unrecognised tokens
        }

        if (current != null) hounds.Add(current);
        return hounds.AsReadOnly();
    }
}

using HamDigiSharp.Abstractions;
using HamDigiSharp.Codecs;
using HamDigiSharp.Models;

namespace HamDigiSharp.Decoders;

/// <summary>
/// Abstract base class for all mode-specific decoders.
/// Provides common state: options, duplicate filter, callsign registration,
/// and the <see cref="ResultAvailable"/> event plumbing.
/// </summary>
public abstract class BaseDecoder : IDigitalModeDecoder
{
    public abstract DigitalMode Mode { get; }
    public event Action<DecodeResult>? ResultAvailable;

    protected DecoderOptions Options { get; private set; } = new();
    protected MessagePacker MessagePacker { get; } = new();

    // ── Duplicate suppression ─────────────────────────────────────────────────
    private const int MaxDup = 240;
    private readonly string[] _dupMsgs = new string[MaxDup];
    private readonly double[] _dupFreqs = new double[MaxDup];
    private int _dupCount;
    private string _lastPeriodTime = "";

    protected BaseDecoder() { Array.Fill(_dupMsgs, ""); }

    public virtual void Configure(DecoderOptions options)
    {
        Options = options;
        MessagePacker.RegisterCallsign(options.MyCall);
        MessagePacker.RegisterCallsign(options.HisCall);
    }

    public abstract IReadOnlyList<DecodeResult> Decode(
        ReadOnlySpan<float> samples, double freqLow, double freqHigh, string utcTime);

    // ── Result emission ───────────────────────────────────────────────────────

    protected void Emit(DecodeResult result)
    {
        if (IsDuplicate(result)) return;
        AddDuplicate(result);
        MessagePacker.RegisterCallsign(ExtractCall(result.Message));
        ResultAvailable?.Invoke(result);
    }

    // ── Duplicate check ───────────────────────────────────────────────────────

    private bool IsDuplicate(DecodeResult r)
    {
        if (r.UtcTime != _lastPeriodTime) { _dupCount = 0; _lastPeriodTime = r.UtcTime; }
        for (int i = 0; i < _dupCount; i++)
            if (_dupMsgs[i] == r.Message && Math.Abs(_dupFreqs[i] - r.FrequencyHz) < 1.0)
                return true;
        return false;
    }

    private void AddDuplicate(DecodeResult r)
    {
        if (_dupCount >= MaxDup) _dupCount = 0;
        _dupMsgs[_dupCount] = r.Message;
        _dupFreqs[_dupCount] = r.FrequencyHz;
        _dupCount++;
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    /// <summary>Extract the first plausible callsign from a decoded message.</summary>
    protected static string ExtractCall(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return "";
        var parts = msg.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            string s = p.Trim('<', '>');
            if (s.Length >= 3 && s.Any(char.IsLetter) && s.Any(char.IsDigit))
                return s;
        }
        return "";
    }

    /// <summary>
    /// Build an AP mask array of length 174.
    /// Positions listed in <paramref name="fixedBits"/> are set to true (fixed by AP).
    /// </summary>
    protected static bool[] BuildApMask(params int[] fixedBits)
    {
        var mask = new bool[174];
        foreach (int i in fixedBits) if (i >= 0 && i < 174) mask[i] = true;
        return mask;
    }

    protected static bool[] EmptyApMask() => new bool[174];

    /// <summary>Compute dB-SNR from decoded hard-error count and approximate noise floor.</summary>
    protected static double EstimateSnr(int hardErrors, double xsnrHint)
        => double.IsNaN(xsnrHint) ? (hardErrors < 0 ? -30 : Math.Clamp(-2 * hardErrors, -30, 10)) : xsnrHint;
}

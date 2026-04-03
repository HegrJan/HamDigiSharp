using HamDigiSharp.Models;

namespace HamDigiSharp.Engine;

/// <summary>
/// UTC-window scheduling helpers for ham radio digital modes.
///
/// Every mode has a fixed transmission period that is synchronised to UTC.
/// For example, FT8 has 15-second periods starting at :00, :15, :30, :45
/// of every minute; JT65A has 60-second periods starting at :00 of every
/// minute; MSK144 has 1-second periods starting at every integer second.
///
/// The schedule is derived purely from the mode's period length and the Unix
/// epoch (1970-01-01T00:00:00Z), so no leap-second or local-timezone knowledge
/// is required.
/// </summary>
public static class PeriodScheduler
{
    // ── Window boundary calculation ───────────────────────────────────────────

    /// <summary>
    /// Returns the UTC start of the current (most recently started) transmission
    /// window for <paramref name="mode"/>.
    /// </summary>
    public static DateTimeOffset CurrentWindowStart(DigitalMode mode, DateTimeOffset utcNow)
    {
        double periodSec  = mode.PeriodSeconds();
        double epochSec   = utcNow.ToUnixTimeMilliseconds() / 1000.0;
        double windowSec  = Math.Floor(epochSec / periodSec) * periodSec;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(windowSec * 1000)).ToUniversalTime();
    }

    /// <summary>
    /// Returns the UTC start of the next (not yet started) transmission window.
    /// </summary>
    public static DateTimeOffset NextWindowStart(DigitalMode mode, DateTimeOffset utcNow)
        => CurrentWindowStart(mode, utcNow).AddSeconds(mode.PeriodSeconds());

    /// <summary>
    /// Returns how many seconds remain until the next window boundary at
    /// <paramref name="utcNow"/>. Always in [0, period).
    /// </summary>
    public static double SecondsToNextWindow(DigitalMode mode, DateTimeOffset utcNow)
        => (NextWindowStart(mode, utcNow) - utcNow).TotalSeconds;

    /// <summary>
    /// Returns the UTC time string ("HHmmss") for the start of the current window.
    /// This is the value to pass to <see cref="DecoderEngine.DecodeAsync"/> as
    /// <c>utcTime</c>.
    /// </summary>
    public static string CurrentWindowUtcString(DigitalMode mode, DateTimeOffset utcNow)
        => CurrentWindowStart(mode, utcNow).ToString("HHmmss");

    // ── Sample-count helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Number of samples at <paramref name="sampleRate"/> that make up exactly
    /// one transmission period for <paramref name="mode"/>. This is the length
    /// of the buffer the decoder expects.
    /// </summary>
    public static int SamplesPerPeriod(DigitalMode mode, int sampleRate)
        => (int)Math.Round(mode.PeriodSeconds() * sampleRate);

    /// <summary>
    /// Number of samples (at <paramref name="captureRate"/>) to skip at the
    /// start of a capture session in order to align the first decode window to
    /// the next UTC boundary.
    ///
    /// Returns 0 if <paramref name="utcNow"/> is already within 50 ms of the
    /// next boundary, OR exactly on a boundary (i.e., remaining ≈ full period).
    /// </summary>
    public static int SamplesToNextBoundary(
        DigitalMode mode, int captureRate, DateTimeOffset utcNow)
    {
        double period    = mode.PeriodSeconds();
        double remaining = SecondsToNextWindow(mode, utcNow);

        // On a boundary: remaining ≈ 0 (just crossed) OR ≈ period (exactly on boundary).
        // In either case, skip nothing — start accumulating immediately.
        if (remaining < 0.05 || remaining > period - 0.05) return 0;
        return (int)Math.Round(remaining * captureRate);
    }
}

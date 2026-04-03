// MshvDecoder — C# port of MSHV decoding logic (GPL)
// Algorithms originally from WSJT-X (K1JT et al.) and MSHV (LZ2HV).
namespace HamDigiSharp.Models;

/// <summary>
/// All 19 MSHV digital modes plus SuperFox — 20 modes in total, in their canonical index order.
/// </summary>
public enum DigitalMode
{
    MSK144  = 0,
    JTMS    = 1,
    FSK441  = 2,
    FSK315  = 3,
    IscatA  = 4,
    IscatB  = 5,
    JT6M    = 6,
    JT65A   = 7,
    JT65B   = 8,
    JT65C   = 9,
    PI4     = 10,
    FT8     = 11,
    MSKMS   = 12,
    FT4     = 13,
    Q65A    = 14,
    Q65B    = 15,
    Q65C    = 16,
    Q65D    = 17,
    FT2     = 18,
    /// <summary>Multi-station Fox/Hound protocol within FT8 timing.</summary>
    SuperFox = 19,
}

public static class DigitalModeExtensions
{
    /// <summary>Returns the nominal TX/RX period in seconds.</summary>
    public static double PeriodSeconds(this DigitalMode mode) => mode switch
    {
        DigitalMode.MSK144 or DigitalMode.MSKMS => 1.0,
        DigitalMode.JTMS   => 15.0,  // 15-second RX window (burst is shorter, window is 15s)
        DigitalMode.FSK441 or DigitalMode.FSK315 => 15.0, // meteor scatter, 15-second RX window
        DigitalMode.IscatA or DigitalMode.IscatB => 30.0, // 30-second RX window
        DigitalMode.JT6M   => 60.0,  // identical to JT65A at the physical layer
        DigitalMode.JT65A or DigitalMode.JT65B or DigitalMode.JT65C => 60.0,
        DigitalMode.PI4    => 30.0,
        DigitalMode.FT8    => 15.0,
        DigitalMode.FT4    => 7.5,
        DigitalMode.Q65A   => 60.0,
        DigitalMode.Q65B   => 30.0,
        DigitalMode.Q65C   => 15.0,
        DigitalMode.Q65D   => 7.5,
        DigitalMode.FT2    => 3.75,  // 3.75s window (NMax=45000/12000); two stations alternate in 7.5s cycles
        DigitalMode.SuperFox => 15.0, // same period as FT8
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    /// <summary>Returns the internal sample rate used by the decoder for this mode.</summary>
    public static int DecoderSampleRate(this DigitalMode mode) => mode switch
    {
        DigitalMode.FT8      => 12000,
        DigitalMode.FT4      => 12000,
        DigitalMode.FT2      => 12000,
        DigitalMode.SuperFox => 12000,
        DigitalMode.MSK144   => 12000,
        DigitalMode.MSKMS    => 12000,
        DigitalMode.Q65A     => 12000,
        DigitalMode.Q65B     => 12000,
        DigitalMode.Q65C     => 12000,
        DigitalMode.Q65D     => 12000,
        _                    => 11025, // JT65A/B/C, JT6M, PI4, IscatA/B, FSK441, FSK315, JTMS
    };

    /// <summary>
    /// Returns the duration of the transmitted signal in seconds.
    /// This is shorter than <see cref="PeriodSeconds"/> — the remainder is silence,
    /// allowing the decoder to start before the period ends.
    /// </summary>
    public static double SignalDurationSeconds(this DigitalMode mode) => mode switch
    {
        // FT8:  79 symbols × 1920/12000 s = 12.64 s  (out of 15 s period)
        DigitalMode.FT8 or DigitalMode.SuperFox => 79  * (1920.0 / 12000),
        // FT4:  105 symbols × 576/12000 s = 5.04 s   (out of  7.5 s period)
        DigitalMode.FT4  => 105 * (576.0  / 12000),
        // FT2:  105 symbols × 288/12000 s = 2.52 s   (out of  3.75 s period)
        DigitalMode.FT2  => 105 * (288.0  / 12000),
        // MSK144/MSKMS: 144 symbols × 6/12000 s = 72 ms  (out of 1 s period)
        DigitalMode.MSK144 or DigitalMode.MSKMS => 144 * (6.0 / 12000),
        // JT65 A/B/C and JT6M: 126 symbols × 4096/11025 s ≈ 46.78 s  (out of 60 s period)
        DigitalMode.JT65A or DigitalMode.JT65B or DigitalMode.JT65C
            or DigitalMode.JT6M => 126 * (4096.0 / 11025),
        // Q65: 85 symbols × Nsps/12000 s
        DigitalMode.Q65A => 85 * (6912.0 / 12000),  //  48.96 s  (out of 60 s)
        DigitalMode.Q65B => 85 * (3456.0 / 12000),  //  24.48 s  (out of 30 s)
        DigitalMode.Q65C => 85 * (1728.0 / 12000),  //  12.24 s  (out of 15 s)
        DigitalMode.Q65D => 85 * ( 864.0 / 12000),  //   6.12 s  (out of  7.5 s)
        // PI4: 146 symbols × 1000/11025 s ≈ 13.24 s  (out of 30 s period)
        DigitalMode.PI4  => 146 * (1000.0 / 11025),
        // For all other modes the signal effectively fills the period.
        _ => mode.PeriodSeconds(),
    };

    public static string ToDisplayString(this DigitalMode mode) => mode switch
    {
        DigitalMode.MSK144 => "MSK144",
        DigitalMode.JTMS   => "JTMS",
        DigitalMode.FSK441 => "FSK441",
        DigitalMode.FSK315 => "FSK315",
        DigitalMode.IscatA => "ISCAT-A",
        DigitalMode.IscatB => "ISCAT-B",
        DigitalMode.JT6M   => "JT6M",
        DigitalMode.JT65A  => "JT65A",
        DigitalMode.JT65B  => "JT65B",
        DigitalMode.JT65C  => "JT65C",
        DigitalMode.PI4    => "PI4",
        DigitalMode.FT8    => "FT8",
        DigitalMode.MSKMS  => "MSKMS",
        DigitalMode.FT4    => "FT4",
        DigitalMode.Q65A   => "Q65A",
        DigitalMode.Q65B   => "Q65B",
        DigitalMode.Q65C   => "Q65C",
        DigitalMode.Q65D   => "Q65D",
        DigitalMode.FT2    => "FT2",
        DigitalMode.SuperFox => "SuperFox",
        _ => mode.ToString()
    };
}

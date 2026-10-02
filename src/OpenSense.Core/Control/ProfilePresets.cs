using OpenSense.Core.Hardware;

namespace OpenSense.Core.Control;

/// <summary>A whole-laptop performance setting: Acer's operating mode, the fans and the CPU's power together.</summary>
public enum PerformanceProfile
{
    /// <summary>Quiet and cool: the Quiet mode, CPU held to 35 W.</summary>
    Silent,

    /// <summary>The most speed for the least fan noise: Balanced, CPU held to about 65 W.</summary>
    QuietPerformance,

    /// <summary>Best clocks per watt: Performance mode, CPU held to about 80 W.</summary>
    Optimize,

    /// <summary>The firmware's full power: Turbo mode, the CPU's own limits.</summary>
    Turbo,

    /// <summary>Everything switched up: Turbo mode, fans at full speed, the highest CPU limits OpenSense can set.</summary>
    Unleash,
}

/// <summary>
/// The performance profiles as <see cref="ControlProfile"/> changes. They set the operating mode, the fan mode and the
/// CPU's power, and leave everything else (curves, GPU clock offsets, lighting) as the user has it.
/// </summary>
public static class ProfilePresets
{
    public static ControlProfile Apply(ControlProfile profile, PerformanceProfile preset) => preset switch
    {
        PerformanceProfile.Silent => With(profile, OperatingMode.Quiet, FanControlMode.Auto, CpuPowerPreset.Eco),
        PerformanceProfile.QuietPerformance => With(profile, OperatingMode.Balanced, FanControlMode.Auto, CpuPowerPreset.Quiet),
        PerformanceProfile.Optimize => With(profile, OperatingMode.Performance, FanControlMode.Auto, CpuPowerPreset.Optimize),
        PerformanceProfile.Turbo => With(profile, OperatingMode.Turbo, FanControlMode.Auto, CpuPowerPreset.Turbo),
        PerformanceProfile.Unleash => With(profile, OperatingMode.Turbo, FanControlMode.Max, CpuPowerPreset.Unleash),
        _ => profile,
    };

    /// <summary>The profile <paramref name="profile"/> is set to exactly, or null when it is a mix.</summary>
    public static PerformanceProfile? Detect(ControlProfile profile)
    {
        foreach (var preset in Enum.GetValues<PerformanceProfile>())
        {
            var applied = Apply(profile, preset);
            if (applied.OperatingMode == profile.OperatingMode && applied.Mode == profile.Mode
                && applied.CpuPower.Preset == profile.CpuPower.Preset && profile.CpuPower.CeilingC is null)
                return preset;
        }
        return null;
    }

    private static ControlProfile With(ControlProfile profile, OperatingMode mode, FanControlMode fans, CpuPowerPreset cpu) =>
        profile with { OperatingMode = mode, Mode = fans, CpuPower = new CpuPowerSettings { Preset = cpu } };
}

using OpenSense.Core.Control;
using OpenSense.Core.Hardware;

namespace OpenSense.Core.Tests;

public sealed class ProfilePresetTests
{
    [Theory]
    [InlineData(PerformanceProfile.Silent, OperatingMode.Quiet, FanControlMode.Auto, CpuPowerPreset.Eco)]
    [InlineData(PerformanceProfile.QuietPerformance, OperatingMode.Balanced, FanControlMode.Auto, CpuPowerPreset.Quiet)]
    [InlineData(PerformanceProfile.Optimize, OperatingMode.Performance, FanControlMode.Auto, CpuPowerPreset.Optimize)]
    [InlineData(PerformanceProfile.Turbo, OperatingMode.Turbo, FanControlMode.Auto, CpuPowerPreset.Firmware)]
    [InlineData(PerformanceProfile.Unleash, OperatingMode.Turbo, FanControlMode.Max, CpuPowerPreset.Firmware)]
    public void A_profile_sets_the_mode_the_fans_and_the_cpu(PerformanceProfile preset, OperatingMode mode, FanControlMode fans, CpuPowerPreset cpu)
    {
        var profile = ProfilePresets.Apply(new ControlProfile(), preset);
        Assert.Equal(mode, profile.OperatingMode);
        Assert.Equal(fans, profile.Mode);
        Assert.Equal(cpu, profile.CpuPower.Preset);
        Assert.Equal(preset, ProfilePresets.Detect(profile));
    }

    [Fact]
    public void A_profile_leaves_everything_else_as_the_user_had_it()
    {
        var mine = new ControlProfile
        {
            CoolBoost = true,
            AutoBoost = false,
            ModeKey = ModeKeyAction.TurboToggle,
            GpuClocks = new GpuClockSettings { Fixed = new ClockOffsets(100, 800) },
            BatteryOperatingMode = OperatingMode.Eco,
        };
        var applied = ProfilePresets.Apply(mine, PerformanceProfile.QuietPerformance);
        Assert.Equal(mine with { OperatingMode = OperatingMode.Balanced, CpuPower = new CpuPowerSettings { Preset = CpuPowerPreset.Quiet } }, applied);
    }

    [Fact]
    public void A_mix_is_not_a_profile()
    {
        var turbo = ProfilePresets.Apply(new ControlProfile(), PerformanceProfile.Turbo);
        Assert.Null(ProfilePresets.Detect(turbo with { OperatingMode = OperatingMode.Balanced }));
        Assert.Null(ProfilePresets.Detect(turbo with { CpuPower = new CpuPowerSettings { Preset = CpuPowerPreset.Quiet } }));
        Assert.Null(ProfilePresets.Detect(turbo with { CpuPower = new CpuPowerSettings { CeilingC = 80 } }));
        Assert.Null(ProfilePresets.Detect(new ControlProfile()));
    }
}

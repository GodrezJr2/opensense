using OpenSense.Core.Control;
using OpenSense.Core.Monitoring;

namespace OpenSense.Core.Tests;

public sealed class CpuPowerTests
{
    private sealed class FakeCpu(double pl1, double pl2, double window = 56, bool locked = false) : ICpuPowerControl
    {
        public double Pl1 = pl1, Pl2 = pl2, Window = window;
        public bool Locked = locked;
        public int Writes;

        public PowerLimitRegister? ReadLimits() => new(Pl1, true, true, Window, Pl2, true, false, 0.0024, Locked);

        public bool WriteLimits(double pl1W, double pl2W, double pl1WindowSeconds)
        {
            if (Locked)
                return false;
            (Pl1, Pl2, Window) = (pl1W, pl2W, pl1WindowSeconds);
            Writes++;
            return true;
        }
    }

    private static readonly DateTime Start = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    private static CpuPowerSettings Settings(CpuPowerPreset preset, int? ceiling = null) => new() { Preset = preset, CeilingC = ceiling };

    /// <summary>Steps the controller <paramref name="steps"/> times, a step interval apart, starting after step <paramref name="from"/>.</summary>
    private static CpuPowerTelemetry? Run(CpuPowerController controller, CpuPowerSettings settings, int steps, double temp = 60, bool onAc = true, bool hot = false, int from = 0)
    {
        CpuPowerTelemetry? last = null;
        for (var i = 0; i < steps; i++)
            last = controller.Step(settings, onAc, temp, hot, 100, Start + (from + i + 1) * CpuPowerController.StepEvery);
        return last;
    }

    [Fact]
    public void Firmware_preset_never_touches_the_cpu()
    {
        var cpu = new FakeCpu(115, 157);
        var controller = new CpuPowerController(cpu);
        Assert.Null(Run(controller, Settings(CpuPowerPreset.Firmware), 5));
        Assert.Equal(0, cpu.Writes);
    }

    [Fact]
    public void Limits_go_up_a_few_watts_at_a_time_while_there_is_room()
    {
        var cpu = new FakeCpu(35, 65);
        var controller = new CpuPowerController(cpu);
        var first = Run(controller, Settings(CpuPowerPreset.Optimize), 1)!;
        Assert.Equal(40, cpu.Pl1);
        Assert.Equal(CpuPowerState.Ramping, first.State);
        Run(controller, Settings(CpuPowerPreset.Optimize), 1, from: 1);
        Assert.Equal(45, cpu.Pl1);
        Assert.Equal(CpuPowerPresets.Optimize.Pl1WindowS, cpu.Window);

        var done = Run(controller, Settings(CpuPowerPreset.Optimize), 20, from: 2)!;
        Assert.Equal(80, cpu.Pl1);
        Assert.Equal(115, cpu.Pl2);
        Assert.Equal(CpuPowerState.Holding, done.State);
    }

    [Fact]
    public void Limits_hold_where_they_are_when_the_temperature_has_no_room()
    {
        var cpu = new FakeCpu(35, 65);
        var controller = new CpuPowerController(cpu);
        Run(controller, Settings(CpuPowerPreset.Unleash), 3);
        // Unleash's ceiling is 95: at 92 there is less than the 5 degrees of room. The temperature is an average, so it
        // takes a few steps to be one.
        Run(controller, Settings(CpuPowerPreset.Unleash), 12, temp: 92, from: 3);
        var held = cpu.Pl1;
        var state = Run(controller, Settings(CpuPowerPreset.Unleash), 5, temp: 92, from: 15)!;
        Assert.Equal(held, cpu.Pl1);
        Assert.Equal(CpuPowerState.Holding, state.State);
    }

    [Fact]
    public void A_spike_of_heat_does_not_back_the_limits_off()
    {
        var cpu = new FakeCpu(115, 157);
        var controller = new CpuPowerController(cpu);
        Run(controller, Settings(CpuPowerPreset.Optimize), 12, temp: 70);
        Assert.Equal(80, cpu.Pl1);

        // A burst takes the die to 99 for one step and it is back at 70: an average that is nowhere near the ceiling.
        Run(controller, Settings(CpuPowerPreset.Optimize), 1, temp: 99, from: 12);
        var after = Run(controller, Settings(CpuPowerPreset.Optimize), 4, temp: 70, from: 13)!;

        Assert.Equal(80, cpu.Pl1);
        Assert.NotEqual(CpuPowerState.BackingOff, after.State);
    }

    [Fact]
    public void Heat_that_lasts_backs_the_limits_off_to_a_floor_and_no_further()
    {
        var cpu = new FakeCpu(115, 157);
        var controller = new CpuPowerController(cpu);
        Run(controller, Settings(CpuPowerPreset.Optimize), 12, temp: 70);
        Assert.Equal(80, cpu.Pl1);

        // 90 is over Optimize's ceiling (86), not slowing the CPU itself: the limits go down, but not below 60 % of 80 W.
        var state = Run(controller, Settings(CpuPowerPreset.Optimize), 30, temp: 90, from: 12)!;

        Assert.Equal(80 * CpuPowerController.BackOffFloor, cpu.Pl1);
        Assert.Equal(CpuPowerState.BackingOff, state.State);
        Assert.True(cpu.Pl2 >= cpu.Pl1);
    }

    [Fact]
    public void A_cpu_that_is_slowing_for_heat_may_go_below_the_floor()
    {
        var cpu = new FakeCpu(115, 157);
        var controller = new CpuPowerController(cpu);
        Run(controller, Settings(CpuPowerPreset.Optimize), 12, temp: 70);

        Run(controller, Settings(CpuPowerPreset.Optimize), 30, temp: 99, hot: true, from: 12);

        Assert.True(cpu.Pl1 < 80 * CpuPowerController.BackOffFloor);
    }

    [Fact]
    public void The_limits_come_back_once_it_has_cooled()
    {
        var cpu = new FakeCpu(115, 157);
        var controller = new CpuPowerController(cpu);
        Run(controller, Settings(CpuPowerPreset.Optimize), 12, temp: 70);
        Run(controller, Settings(CpuPowerPreset.Optimize), 30, temp: 90, from: 12);
        Assert.Equal(48, cpu.Pl1);

        var state = Run(controller, Settings(CpuPowerPreset.Optimize), 30, temp: 70, from: 42)!;

        Assert.Equal(80, cpu.Pl1);
        Assert.Equal(CpuPowerState.Holding, state.State);
    }

    [Fact]
    public void Limits_step_down_when_the_cpu_is_at_the_ceiling_or_slowing_for_heat()
    {
        var cpu = new FakeCpu(115, 157);
        var controller = new CpuPowerController(cpu);
        Run(controller, Settings(CpuPowerPreset.Turbo), 1, temp: 95);
        Assert.Equal(110, cpu.Pl1);
        var t = Run(controller, Settings(CpuPowerPreset.Turbo), 1, temp: 70, hot: true, from: 1)!;
        Assert.Equal(105, cpu.Pl1);
        Assert.Equal(CpuPowerState.BackingOff, t.State);
        Assert.True(cpu.Pl2 >= cpu.Pl1);
    }

    [Fact]
    public void Limits_never_go_below_the_floor_however_hot()
    {
        var cpu = new FakeCpu(30, 40);
        var controller = new CpuPowerController(cpu);
        Run(controller, Settings(CpuPowerPreset.Eco), 30, temp: 99, hot: true);
        Assert.Equal(CpuPowerPresets.MinPl1W, cpu.Pl1);
    }

    [Fact]
    public void A_lower_target_is_reached_in_steps()
    {
        var cpu = new FakeCpu(115, 157);
        var controller = new CpuPowerController(cpu);
        Run(controller, Settings(CpuPowerPreset.Eco), 1);
        Assert.Equal(105, cpu.Pl1);
        Run(controller, Settings(CpuPowerPreset.Eco), 12, from: 1);
        Assert.Equal(35, cpu.Pl1);
        Assert.Equal(65, cpu.Pl2);
    }

    [Fact]
    public void On_battery_nothing_goes_above_what_the_firmware_had()
    {
        var cpu = new FakeCpu(60, 100);
        var controller = new CpuPowerController(cpu);
        var t = Run(controller, Settings(CpuPowerPreset.Unleash), 30, onAc: false)!;
        Assert.Equal(60, cpu.Pl1);
        Assert.Equal(100, cpu.Pl2);
        Assert.Equal(CpuPowerState.OnBattery, t.State);
    }

    [Fact]
    public void A_change_by_something_else_is_counted_and_put_back()
    {
        var cpu = new FakeCpu(115, 157);
        var controller = new CpuPowerController(cpu);
        Run(controller, Settings(CpuPowerPreset.Turbo), 2);
        cpu.Pl1 = 45; // the firmware moves it
        var t = Run(controller, Settings(CpuPowerPreset.Turbo), 1, from: 2)!;
        Assert.Equal(1, t.Overwrites);
        Assert.Equal(115, cpu.Pl1);
    }

    [Fact]
    public void A_locked_register_is_refused_and_not_written()
    {
        var cpu = new FakeCpu(115, 157, locked: true);
        var controller = new CpuPowerController(cpu);
        var t = Run(controller, Settings(CpuPowerPreset.Optimize), 3)!;
        Assert.Equal(CpuPowerState.Refused, t.State);
        Assert.Equal(0, cpu.Writes);
    }

    [Fact]
    public void Asking_for_nothing_again_puts_the_firmwares_values_back()
    {
        var cpu = new FakeCpu(115, 157, 56);
        var controller = new CpuPowerController(cpu);
        Run(controller, Settings(CpuPowerPreset.Eco), 12);
        Assert.Equal(35, cpu.Pl1);
        var t = Run(controller, Settings(CpuPowerPreset.Firmware), 1, from: 12)!;
        Assert.Equal((115, 157, 56), (cpu.Pl1, cpu.Pl2, cpu.Window));
        Assert.Equal(CpuPowerState.Idle, t.State);
        Assert.Null(Run(controller, Settings(CpuPowerPreset.Firmware), 1, from: 13));
    }

    [Fact]
    public void Stopping_puts_the_firmwares_values_back()
    {
        var cpu = new FakeCpu(115, 157, 56);
        var controller = new CpuPowerController(cpu);
        Run(controller, Settings(CpuPowerPreset.Optimize), 6);
        controller.Restore();
        Assert.Equal((115, 157, 56), (cpu.Pl1, cpu.Pl2, cpu.Window));
    }

    [Fact]
    public void The_ceiling_stays_under_the_cpus_own_thermal_limit()
    {
        var cpu = new FakeCpu(115, 157);
        var controller = new CpuPowerController(cpu);
        // A target ceiling of 90 against a thermal limit of 85 backs off from 84, not from 89.
        controller.Step(Settings(CpuPowerPreset.Turbo), true, 84, false, 85, Start + CpuPowerController.StepEvery);
        Assert.Equal(110, cpu.Pl1);
    }

    [Fact]
    public void Settings_are_held_inside_the_bounds()
    {
        var wild = new CpuPowerSettings { Preset = CpuPowerPreset.Custom, Custom = new CpuPowerTarget(500, 10, 1000, 120) };
        var target = wild.Target()!;
        Assert.Equal(CpuPowerPresets.MaxPl1W, target.Pl1W);
        Assert.Equal(CpuPowerPresets.MaxPl1W, target.Pl2W);
        Assert.Equal(CpuPowerPresets.MaxWindowS, target.Pl1WindowS);
        Assert.Equal(CpuPowerPresets.MaxCeilingC, target.CeilingC);
        Assert.Null(new CpuPowerSettings { Preset = CpuPowerPreset.Custom }.Target());
        Assert.Null(new CpuPowerSettings().Target());
    }

    [Fact]
    public void The_temperature_target_can_be_set_for_any_preset()
    {
        Assert.Equal(80, new CpuPowerSettings { Preset = CpuPowerPreset.Turbo, CeilingC = 80 }.Target()!.CeilingC);
        Assert.Equal(CpuPowerPresets.Turbo.CeilingC, new CpuPowerSettings { Preset = CpuPowerPreset.Turbo }.Target()!.CeilingC);
    }

    [Fact]
    public void Presets_rise_in_power_and_all_sit_inside_the_bounds()
    {
        CpuPowerTarget[] ladder = [CpuPowerPresets.Eco, CpuPowerPresets.Quiet, CpuPowerPresets.Optimize, CpuPowerPresets.Turbo, CpuPowerPresets.Unleash];
        for (var i = 0; i < ladder.Length; i++)
        {
            Assert.Equal(ladder[i], CpuPowerPresets.Clamp(ladder[i]));
            if (i > 0)
            {
                Assert.True(ladder[i].Pl1W > ladder[i - 1].Pl1W);
                Assert.True(ladder[i].CeilingC >= ladder[i - 1].CeilingC);
            }
        }
    }

    [Theory]
    [InlineData(56)]
    [InlineData(28)]
    [InlineData(96)]
    [InlineData(128)]
    public void Time_windows_survive_encoding(double seconds)
    {
        const double unit = 1.0 / 1024;
        var register = CpuTuningDecode.WithPowerLimits(0, 50, 60, seconds, 0.125, unit);
        Assert.Equal(seconds, CpuTuningDecode.PowerLimits(register, 0.125, unit).Pl1Window!.Value, 6);
    }

    [Fact]
    public void Writing_limits_keeps_the_other_bits_of_the_register()
    {
        const ulong original = 0x004284E8_00DF8398UL;
        var before = CpuTuningDecode.PowerLimits(original, 0.125, 1.0 / 1024);
        var written = CpuTuningDecode.WithPowerLimits(original, 80, 115, 56, 0.125, 1.0 / 1024);
        var limits = CpuTuningDecode.PowerLimits(written, 0.125, 1.0 / 1024);
        Assert.Equal(80, limits.Pl1W);
        Assert.Equal(115, limits.Pl2W);
        Assert.Equal(before.Pl1Clamp, limits.Pl1Clamp);
        Assert.False(limits.Locked);
        // PL2's clamp and window are as they were.
        Assert.Equal(before.Pl2Window, limits.Pl2Window);
        Assert.Equal(before.Pl2Clamp, limits.Pl2Clamp);
    }

    [Fact]
    public void Writing_limits_leaves_a_lock_bit_alone()
    {
        var written = CpuTuningDecode.WithPowerLimits(1UL << 63, 80, 115, 56, 0.125, 1.0 / 1024);
        Assert.True(CpuTuningDecode.PowerLimits(written, 0.125, 1.0 / 1024).Locked);
    }
}

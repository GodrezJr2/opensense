using OpenSense.Core.Analysis;
using OpenSense.Core.Control;
using OpenSense.Core.Hardware;
using OpenSense.Core.Monitoring;

namespace OpenSense.Core.Tests;

public sealed class ThermalAnalyzerTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static List<Telemetry> Run(int seconds, Func<int, Telemetry, Telemetry> shape)
    {
        var baseSample = new Telemetry
        {
            Timestamp = Start,
            CpuTemperature = 70,
            CpuLoad = 90,
            CpuPower = 60,
            GpuTemperature = 70,
            GpuLoad = 90,
            GpuPower = 90,
            OperatingMode = OperatingMode.Turbo,
            Fans = [new FanTelemetry(FanId.Cpu, 4500, FanBehavior.Auto, null), new FanTelemetry(FanId.Gpu, 5000, FanBehavior.Auto, null)],
        };
        return [.. Enumerable.Range(0, seconds).Select(i => shape(i, baseSample with { Timestamp = Start.AddSeconds(i) }))];
    }

    [Fact]
    public void Too_few_samples_are_not_judged()
    {
        var report = ThermalAnalyzer.Analyze(Run(3, (_, s) => s));
        Assert.Equal("not-enough-data", report.Verdict);
    }

    [Fact]
    public void A_cool_loaded_run_is_healthy()
    {
        var report = ThermalAnalyzer.Analyze(Run(60, (_, s) => s));
        Assert.Equal("healthy", report.Verdict);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void A_run_with_no_load_says_so()
    {
        var report = ThermalAnalyzer.Analyze(Run(60, (_, s) => s with { CpuLoad = 3, GpuLoad = 1 }));
        Assert.Equal("not-enough-load", report.Verdict);
    }

    [Fact]
    public void Touching_the_cpu_limit_is_the_thermal_wall()
    {
        var report = ThermalAnalyzer.Analyze(Run(60, (_, s) => s with { CpuTemperature = 99 }));
        Assert.Equal("thermal-limited", report.Verdict);
        Assert.Contains(report.Findings, f => f is { Area: "cpu", Severity: Severity.Warning });
    }

    [Fact]
    public void The_cpu_reporting_thermal_throttling_is_a_warning_with_its_share_of_samples()
    {
        var report = ThermalAnalyzer.Analyze(Run(40, (i, s) => i % 2 == 0 ? s with { CpuThrottle = CpuThrottleReason.Thermal } : s));
        Assert.Contains(report.Findings, f => f.Area == "cpu" && f.Summary.Contains("thermally throttling") && f.Evidence.Contains("50%"));
    }

    [Fact]
    public void Power_limit_throttling_without_heat_is_power_limited()
    {
        var report = ThermalAnalyzer.Analyze(Run(60, (_, s) => s with { CpuThrottle = CpuThrottleReason.PowerLimit, CpuPowerLimit1 = 55, CpuPowerLimit2 = 157 }));
        Assert.Equal("power-limited", report.Verdict);
        Assert.Contains(report.Findings, f => f.Evidence.Contains("PL1 55 W"));
    }

    [Fact]
    public void A_gpu_power_cap_under_load_is_normal_not_thermal()
    {
        var report = ThermalAnalyzer.Analyze(Run(60, (_, s) => s with { GpuThrottleReasons = GpuThrottle.SoftwarePowerCap }));
        Assert.Equal("power-limited", report.Verdict);
        Assert.Contains(report.Findings, f => f.Area == "gpu" && f.Severity == Severity.Notice);
    }

    [Fact]
    public void A_gpu_thermal_slowdown_is_the_thermal_wall()
    {
        var report = ThermalAnalyzer.Analyze(Run(60, (_, s) => s with { GpuThrottleReasons = GpuThrottle.HardwareThermalSlowdown }));
        Assert.Equal("thermal-limited", report.Verdict);
    }

    [Fact]
    public void Maxed_fans_with_hot_chips_mean_saturated_cooling()
    {
        var report = ThermalAnalyzer.Analyze(Run(60, (_, s) => s with
        {
            CpuTemperature = 93,
            Fans = [new FanTelemetry(FanId.Cpu, 6000, FanBehavior.Max, 100)],
        }));
        Assert.Contains(report.Findings, f => f.Area == "fans" && f.Summary.Contains("saturated"));
    }

    [Fact]
    public void A_low_power_mode_under_load_is_pointed_out()
    {
        var report = ThermalAnalyzer.Analyze(Run(60, (_, s) => s with { OperatingMode = OperatingMode.Quiet }));
        Assert.Contains(report.Findings, f => f.Area == "mode");
    }

    [Fact]
    public void Falling_cpu_power_during_load_is_boost_fading()
    {
        var report = ThermalAnalyzer.Analyze(Run(90, (i, s) => s with { CpuPower = i < 30 ? 120 : i < 60 ? 90 : 60 }));
        Assert.Contains(report.Findings, f => f.Summary.Contains("boost fading"));
    }

    [Fact]
    public void The_temperature_trend_is_in_degrees_per_minute()
    {
        var report = ThermalAnalyzer.Analyze(Run(60, (i, s) => s with { CpuTemperature = 60 + i * 0.5 }));
        Assert.Equal(30, report.Cpu.TemperatureTrendPerMinute!.Value, 3);
    }

    [Fact]
    public void A_short_spike_to_the_limit_is_not_a_thermal_wall()
    {
        // A shader compile: 4 s at 100 °C (and thermal throttling) in a 120 s run that otherwise sits at 70.
        var report = ThermalAnalyzer.Analyze(Run(120, (i, s) => i is >= 50 and < 54
            ? s with { CpuTemperature = 100, CpuThrottle = CpuThrottleReason.Thermal }
            : s));

        Assert.NotEqual("thermal-limited", report.Verdict);
        var spike = Assert.Single(report.Findings, f => f.Code == "thermal-spike");
        Assert.Equal(Severity.Notice, spike.Severity);
        Assert.Equal(70, report.Cpu.TemperatureMedian);
        Assert.Equal(100, report.Cpu.TemperatureMax);
        var episode = Assert.Single(report.ThrottleEpisodes);
        Assert.Equal(("CPU", "thermal", 50.0, 4.0), (episode.Chip, episode.Kind, episode.StartSeconds, episode.DurationSeconds));
    }

    [Fact]
    public void Long_stretches_at_the_limit_are_a_thermal_wall_even_without_the_throttle_flag()
    {
        var report = ThermalAnalyzer.Analyze(Run(120, (i, s) => i >= 60 ? s with { CpuTemperature = 99 } : s));

        Assert.Equal("thermal-limited", report.Verdict);
        var finding = Assert.Single(report.Findings, f => f.Code == "near-limit");
        Assert.Equal(60, report.Cpu.SecondsNearLimit);
        Assert.Equal(60, report.Cpu.LongestNearLimitSeconds);
        Assert.Contains("60 s", finding.Evidence);
    }

    [Fact]
    public void Sitting_close_under_the_limit_without_throttling_is_running_hot()
    {
        var report = ThermalAnalyzer.Analyze(Run(60, (_, s) => s with { CpuTemperature = 92 }));

        Assert.Equal("running-hot", report.Verdict);
        Assert.Contains(report.Findings, f => f.Code == "hot");
    }

    [Fact]
    public void The_summary_says_where_the_chip_sits_not_just_the_peak()
    {
        var report = ThermalAnalyzer.Analyze(Run(100, (i, s) => s with { CpuTemperature = 60 + i % 10, CpuPower = 50 + i % 5, CpuClockMhz = 4000 }));

        Assert.Equal(64.5, report.Cpu.TemperatureMedian);
        Assert.Equal(69, report.Cpu.Temperature95th!.Value, 0);
        Assert.Equal(69, report.Cpu.TemperatureMax);
        Assert.Equal(52, report.Cpu.PowerMedianW);
        Assert.Equal(52, report.Cpu.LoadedPowerMedianW);
        Assert.Equal(4000, report.Cpu.LoadedClockMedianMhz);
        Assert.Equal(1.0, report.Cpu.TemperatureBands.Values.Sum(), 2);
        Assert.Equal(1.0, report.Cpu.TemperatureBands["30+ under limit"], 2);
    }

    [Fact]
    public void The_timeline_shows_a_change_over_the_run()
    {
        var report = ThermalAnalyzer.Analyze(Run(80, (i, s) => s with { CpuTemperature = 60 + i / 2.0, CpuPower = i < 40 ? 120 : 60 }));

        Assert.Equal(8, report.Timeline.Count);
        Assert.True(report.Timeline[^1].CpuTemperatureC > report.Timeline[0].CpuTemperatureC);
        Assert.Equal(120, report.Timeline[0].CpuPowerW);
        Assert.Equal(60, report.Timeline[^1].CpuPowerW);
        Assert.Equal(0, report.Timeline[0].StartSeconds);
    }

    [Fact]
    public void A_hot_cpu_held_far_under_pl1_points_at_cooling()
    {
        // Load never reaches 60%, yet the power-limit flag is on while power sits at 60 W of a 115 W PL1 and the chip is hot.
        var report = ThermalAnalyzer.Analyze(Run(60, (_, s) => s with
        {
            CpuLoad = 30, GpuLoad = 90, CpuTemperature = 90, CpuPower = 60, CpuPowerLimit1 = 115, CpuThrottle = CpuThrottleReason.PowerLimit,
        }));

        var finding = Assert.Single(report.Findings, f => f.Code == "power-limit");
        Assert.Contains("points at cooling", finding.Evidence);
    }

    [Fact]
    public void Gpu_reasons_the_model_does_not_know_are_still_named()
    {
        Assert.Equal(["other (0x1000)"], GpuThrottle.Describe(0x1000));
    }

    [Fact]
    public void Decoding_the_package_status_register()
    {
        Assert.Equal(CpuThrottleReason.None, IntelPackageSensor.DecodeThrottle(0));
        Assert.Equal(CpuThrottleReason.Thermal | CpuThrottleReason.PowerLimit, IntelPackageSensor.DecodeThrottle((1UL << 0) | (1UL << 10)));
    }

    [Fact]
    public void History_keeps_the_newest_samples_and_trims_by_window()
    {
        var history = new OpenSense.Core.Engine.TelemetryHistory(capacity: 10);
        foreach (var sample in Run(20, (_, s) => s))
            history.Add(sample);
        Assert.Equal(10, history.Last(TimeSpan.FromHours(1)).Count);
        Assert.Equal(4, history.Last(TimeSpan.FromSeconds(3)).Count);
    }

    [Fact]
    public void Gpu_throttle_reasons_have_names()
    {
        Assert.Equal(["power cap (software)", "thermal slowdown (hardware)"], GpuThrottle.Describe(GpuThrottle.SoftwarePowerCap | GpuThrottle.HardwareThermalSlowdown));
    }
}

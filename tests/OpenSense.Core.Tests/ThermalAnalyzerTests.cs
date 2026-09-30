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

using OpenSense.Core.Analysis;
using OpenSense.Core.Control;
using OpenSense.Core.Hardware;
using OpenSense.Core.Monitoring;

namespace OpenSense.Core.Tests;

public sealed class HardwareProfileTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ProfileInput Input(string? cpu = "Intel(R) Core(TM) i9-14900HX", string? gpu = "NVIDIA GeForce RTX 4060 Laptop GPU",
        string? model = "Acer Predator PHN16-72", double? tgp = 113.6) =>
        new(model, cpu, gpu, 24, 32, 55, 115, 157, tgp, 115, 8, 2, 31.7);

    [Theory]
    [InlineData("Intel(R) Core(TM) i9-14900HX", "Intel", "HX", "14th gen")]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700H", "Intel", "H", "13th gen")]
    [InlineData("Intel(R) Core(TM) i5-1235U", "Intel", "U", "12th gen")]
    [InlineData("Intel(R) Core(TM) i7-8750H", "Intel", "H", "8th gen")]
    [InlineData("Intel(R) Core(TM) i7-11800H", "Intel", "H", "11th gen")]
    [InlineData("Intel(R) Core(TM) Ultra 9 185H", "Intel", "H", "Core Ultra")]
    [InlineData("AMD Ryzen 9 7945HX3D 16-Core Processor", "AMD", "HX", "7000 series")]
    [InlineData("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", "AMD", "HS", "7000 series")]
    public void Recognises_the_cpu_series(string name, string vendor, string series, string generation)
    {
        var cpu = HardwareProfiler.Describe(Input(cpu: name)).Cpu;
        Assert.Equal((vendor, series, generation), (cpu.Vendor, cpu.Series, cpu.Generation));
        Assert.NotNull(cpu.TypicalSustainedTemperature);
    }

    [Fact]
    public void An_unrecognised_cpu_gets_no_typical_range_rather_than_a_guess()
    {
        var cpu = HardwareProfiler.Describe(Input(cpu: "Some Future Processor")).Cpu;
        Assert.Null(cpu.Series);
        Assert.Null(cpu.TypicalSustainedTemperature);
    }

    [Fact]
    public void Measured_facts_pass_through_unchanged()
    {
        var profile = HardwareProfiler.Describe(Input());

        Assert.Equal(24, profile.Cpu.PhysicalCores);
        Assert.Equal(55, profile.Cpu.BasePowerW);
        Assert.Equal(115, profile.Cpu.SustainedPowerLimitW);
        Assert.Equal(113.6, profile.Gpu!.MaxPowerW);
        Assert.Equal(8, profile.Gpu.MemoryGb);
        Assert.Equal(2, profile.Laptop.FanCount);
        Assert.Equal(115 + 113.6, profile.WorstCaseCombinedPowerW!.Value, 6);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4050 Laptop GPU", "entry")]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", "mainstream")]
    [InlineData("NVIDIA GeForce RTX 4070 Laptop GPU", "upper-mainstream")]
    [InlineData("NVIDIA GeForce RTX 4080 Laptop GPU", "high-end")]
    [InlineData("NVIDIA GeForce RTX 4090 Laptop GPU", "enthusiast")]
    public void Ranks_the_gpu_by_model(string name, string tier) =>
        Assert.Equal(tier, HardwareProfiler.Describe(Input(gpu: name)).Gpu!.Tier);

    [Fact]
    public void Says_where_the_laptops_gpu_power_limit_sits_in_the_models_range()
    {
        Assert.Contains("top of the range", HardwareProfiler.Describe(Input(tgp: 113.6)).Gpu!.PowerNote);
        Assert.Contains("low end", HardwareProfiler.Describe(Input(tgp: 45)).Gpu!.PowerNote);
    }

    [Theory]
    [InlineData("Acer Predator PHN16-72", "Predator Helios Neo", "mainstream gaming")]
    [InlineData("Acer Predator PH18-72", "Predator Helios", "flagship gaming")]
    [InlineData("Acer Predator PT516-52s", "Predator Triton", "premium thin gaming")]
    [InlineData("Acer Nitro AN515-57", "Nitro", "entry gaming")]
    public void Places_the_laptop_in_its_line(string model, string line, string tier)
    {
        var laptop = HardwareProfiler.Describe(Input(model: model)).Laptop;
        Assert.Equal((line, tier), (laptop.Line, laptop.Tier));
    }

    [Fact]
    public void Says_how_much_is_measured_and_how_much_is_a_rule_of_thumb()
    {
        Assert.Contains("rules of thumb", HardwareProfiler.Describe(Input()).Caveat);
    }

    private static List<Telemetry> Run(double cpuTemperature, int seconds = 60) =>
        [.. Enumerable.Range(0, seconds).Select(i => new Telemetry
        {
            Timestamp = Start.AddSeconds(i),
            CpuTemperature = cpuTemperature,
            CpuLoad = 90,
            CpuPower = 60,
            GpuTemperature = 70,
            GpuLoad = 90,
            GpuPower = 90,
        })];

    [Fact]
    public void A_hx_cpu_at_91_is_typical_for_its_class_and_is_not_called_running_hot()
    {
        var profile = HardwareProfiler.Describe(Input());
        var report = ThermalAnalyzer.Analyze(Run(91), profile);

        Assert.NotEqual("running-hot", report.Verdict);
        Assert.Contains(report.Findings, f => f.Code == "hot-typical" && f.Severity == Severity.Info);
        Assert.Contains(report.Findings, f => f.Code == "class-typical");
        Assert.Contains(report.Context, line => line.Contains("HX-series"));
    }

    [Fact]
    public void The_same_temperature_on_a_u_series_chip_is_hotter_than_usual()
    {
        var profile = HardwareProfiler.Describe(Input(cpu: "Intel(R) Core(TM) i5-1235U"));
        var report = ThermalAnalyzer.Analyze(Run(92), profile);

        Assert.Equal("running-hot", report.Verdict);
        Assert.Contains(report.Findings, f => f.Code == "class-hot");
    }

    [Fact]
    public void Without_a_profile_the_judgement_is_unchanged()
    {
        var report = ThermalAnalyzer.Analyze(Run(91));

        Assert.Equal("running-hot", report.Verdict);
        Assert.Empty(report.Context);
    }

    [Fact]
    public void A_gpu_holding_its_boost_target_is_not_a_thermal_wall_but_the_unused_power_is_reported()
    {
        var samples = Run(70).Select(s => s with { GpuTemperature = 86, GpuPower = 97, GpuPowerLimit = 113.6 }).ToList();
        var report = ThermalAnalyzer.Analyze(samples, HardwareProfiler.Describe(Input()));

        Assert.NotEqual("thermal-limited", report.Verdict);
        var finding = Assert.Single(report.Findings, f => f.Code == "gpu-at-target");
        Assert.Equal(Severity.Notice, finding.Severity);
        Assert.Contains("17 W of power headroom", finding.Evidence);
    }

    [Fact]
    public void A_real_driver_thermal_slowdown_is_still_the_wall()
    {
        var samples = Run(70).Select(s => s with { GpuTemperature = 86, GpuThrottleReasons = GpuThrottle.HardwareThermalSlowdown }).ToList();
        Assert.Equal("thermal-limited", ThermalAnalyzer.Analyze(samples, HardwareProfiler.Describe(Input())).Verdict);
    }

    [Fact]
    public void Reports_how_much_of_the_power_budget_is_used()
    {
        var report = ThermalAnalyzer.Analyze(Run(70), HardwareProfiler.Describe(Input()));
        var budget = Assert.Single(report.Findings, f => f.Code == "budget");
        Assert.Contains("150 W", budget.Evidence);
        Assert.Contains("229 W", budget.Evidence);
    }
}

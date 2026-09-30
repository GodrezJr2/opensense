using OpenSense.Core.Control;
using OpenSense.Core.Hardware;
using OpenSense.Core.Monitoring;

namespace OpenSense.Core.Analysis;

public enum Severity
{
    Info,
    Notice,
    Warning,
    Critical,
}

/// <param name="Area">cpu, gpu, fans, power, mode or data.</param>
/// <param name="Summary">What was seen, in a sentence.</param>
/// <param name="Evidence">The numbers behind it.</param>
public sealed record Finding(Severity Severity, string Area, string Summary, string Evidence);

/// <summary>One chip over the window: min/average/max of what was read.</summary>
public sealed record ChipSummary(
    string Name,
    double? TemperatureAvg,
    double? TemperatureMax,
    int? TemperatureLimit,
    double? LoadAvg,
    double? PowerAvg,
    double? PowerMax,
    double? ClockAvgMhz,
    double? ClockMaxMhz,
    double? TemperatureTrendPerMinute);

public sealed record FanSummary(string Fan, double? RpmAvg, int? RpmMax, double FractionAtFullBoost);

/// <param name="Verdict">healthy, warm, power-limited, thermal-limited or not-enough-load.</param>
public sealed record ThermalReport(
    string Verdict,
    string Headline,
    int Samples,
    double WindowSeconds,
    string? OperatingMode,
    bool? OnAcPower,
    ChipSummary Cpu,
    ChipSummary? Gpu,
    IReadOnlyList<FanSummary> Fans,
    double? TotalPowerAvg,
    double? TotalPowerMax,
    IReadOnlyList<Finding> Findings);

/// <summary>
/// Reads a run of telemetry the way someone troubleshooting a laptop would: is a chip at its thermal limit, is something
/// capping its power, are the fans already flat out. Pure, so it is testable and an AI agent only has to interpret it.
/// </summary>
public static class ThermalAnalyzer
{
    public const int MinimumSamples = 5;
    private const double Busy = 60;       // percent load that counts as a real workload
    private const double NearLimit = 3;   // degrees under a limit that count as "at the wall"
    private const double Often = 0.25;    // fraction of samples that makes a flag a pattern

    public static ThermalReport Analyze(IReadOnlyList<Telemetry> samples)
    {
        if (samples.Count < MinimumSamples)
        {
            return new ThermalReport("not-enough-data", "Fewer than " + MinimumSamples + " samples; wait a few seconds and ask again.", samples.Count, 0,
                null, null, Empty("CPU"), null, [], null, null, []);
        }

        var last = samples[^1];
        var seconds = (last.Timestamp - samples[0].Timestamp).TotalSeconds;
        var findings = new List<Finding>();
        var limits = last.Limits;

        var cpu = Summarise("CPU", samples, limits.Cpu, s => s.CpuTemperature, s => s.CpuLoad, s => s.CpuPower, s => s.CpuClockMhz);
        var hasGpu = samples.Any(s => s.GpuTemperature is not null || s.GpuPower is not null);
        var gpu = hasGpu
            ? Summarise("GPU", samples, limits.Gpu, s => s.GpuTemperature, s => s.GpuLoad, s => s.GpuPower, s => s.GpuClockMhz)
            : null;
        var fans = SummariseFans(samples);
        var total = samples.Select(s => s.TotalPower).OfType<double>().ToList();

        var busyCpu = Fraction(samples, s => s.CpuLoad >= Busy);
        var busyGpu = Fraction(samples, s => s.GpuLoad >= Busy);
        var loaded = busyCpu >= Often || busyGpu >= Often;

        Cpu(findings, samples, cpu, last, busyCpu);
        if (gpu is not null)
            Gpu(findings, samples, gpu, busyGpu);
        Fans(findings, samples, cpu, gpu, fans);
        Mode(findings, samples, last, loaded);

        if (!loaded)
        {
            findings.Add(new Finding(Severity.Info, "data", "The window has no sustained heavy load, so limits were not exercised.",
                $"CPU load was 60%+ in {busyCpu:P0} of samples and GPU load in {busyGpu:P0}. Run the game or benchmark, then analyse again."));
        }

        var (verdict, headline) = Decide(findings, loaded);
        return new ThermalReport(verdict, headline, samples.Count, Math.Round(seconds), last.OperatingMode?.ToString(), last.OnAcPower, cpu, gpu, fans,
            total.Count > 0 ? Math.Round(total.Average(), 1) : null, total.Count > 0 ? Math.Round(total.Max(), 1) : null, findings);
    }

    private static void Cpu(List<Finding> findings, IReadOnlyList<Telemetry> samples, ChipSummary cpu, Telemetry last, double busy)
    {
        var thermal = Fraction(samples, s => (s.CpuThrottle & (CpuThrottleReason.Thermal | CpuThrottleReason.Prochot | CpuThrottleReason.Critical)) != 0);
        var power = Fraction(samples, s => (s.CpuThrottle & CpuThrottleReason.PowerLimit) != 0);
        var current = Fraction(samples, s => (s.CpuThrottle & CpuThrottleReason.CurrentLimit) != 0);

        if (samples.Any(s => (s.CpuThrottle & CpuThrottleReason.Critical) != 0))
        {
            findings.Add(new Finding(Severity.Critical, "cpu", "The CPU reached its critical temperature.",
                $"Peak {cpu.TemperatureMax:0} °C against a throttle point of {cpu.TemperatureLimit} °C."));
        }
        if (thermal >= 0.05)
        {
            findings.Add(new Finding(Severity.Warning, "cpu", "The CPU is thermally throttling (at its temperature limit or PROCHOT).",
                $"The package reported thermal throttling in {thermal:P0} of samples; peak {cpu.TemperatureMax:0} °C, limit {cpu.TemperatureLimit} °C."));
        }
        else if (cpu.TemperatureMax >= cpu.TemperatureLimit - NearLimit)
        {
            findings.Add(new Finding(Severity.Warning, "cpu", "The CPU is touching its throttle point.",
                $"Peak {cpu.TemperatureMax:0} °C, within {NearLimit:0} °C of the {cpu.TemperatureLimit} °C limit."));
        }
        else if (cpu.TemperatureMax >= cpu.TemperatureLimit - 10)
        {
            findings.Add(new Finding(Severity.Notice, "cpu", "The CPU runs hot but has a little headroom.",
                $"Peak {cpu.TemperatureMax:0} °C, {cpu.TemperatureLimit - cpu.TemperatureMax:0} °C under the limit."));
        }

        if (power >= Often)
        {
            var limits = last.CpuPowerLimit1 is { } pl1 ? $" (PL1 {pl1:0} W" + (last.CpuPowerLimit2 is { } pl2 ? $", PL2 {pl2:0} W)" : ")") : "";
            var belowPl1 = last.CpuPowerLimit1 is { } p1 && cpu.PowerAvg is { } avg && avg < p1 * 0.8;
            var hot = cpu.TemperatureMax >= cpu.TemperatureLimit - 10;
            var advice = belowPl1 && hot
                ? "Average power sits well below PL1, so the limit in force is lower than PL1: the firmware is most likely cutting power to manage heat, which points at cooling rather than at the mode."
                : belowPl1
                    ? "Average power sits well below PL1, so the limit in force is lower than PL1 (set dynamically by the firmware)."
                    : "If the temperature has room, a higher operating mode may raise the limit.";
            findings.Add(new Finding(Severity.Notice, "cpu", "A power limit is holding the CPU's clocks down.",
                $"Power-limit throttling in {power:P0} of samples{limits}; average {cpu.PowerAvg:0} W. {advice}"));
        }
        if (current >= Often)
        {
            findings.Add(new Finding(Severity.Notice, "cpu", "The CPU hit its current limit (ICCmax).", $"Current-limit throttling in {current:P0} of samples."));
        }

        if (busy >= Often)
        {
            var fade = PowerFade(samples, s => s.CpuLoad, s => s.CpuPower);
            if (fade is { } f && f >= 0.15)
            {
                findings.Add(new Finding(Severity.Notice, "cpu", "CPU power fell during the load (boost fading).",
                    $"Average power dropped {f:P0} from the first third of the loaded samples to the last third; typical of PL2 giving way to PL1 or of heat soak."));
            }
        }
    }

    private static void Gpu(List<Finding> findings, IReadOnlyList<Telemetry> samples, ChipSummary gpu, double busy)
    {
        var thermal = Fraction(samples, s => s.GpuThrottleReasons is { } r && (r & (GpuThrottle.SoftwareThermalSlowdown | GpuThrottle.HardwareThermalSlowdown)) != 0);
        var brake = Fraction(samples, s => s.GpuThrottleReasons is { } r && (r & (GpuThrottle.HardwarePowerBrake | GpuThrottle.HardwareSlowdown)) != 0);
        var cap = Fraction(samples, s => s.GpuThrottleReasons is { } r && (r & GpuThrottle.SoftwarePowerCap) != 0);

        if (thermal >= 0.05)
        {
            findings.Add(new Finding(Severity.Warning, "gpu", "The GPU is thermally throttling.",
                $"The driver reported a thermal slowdown in {thermal:P0} of samples; peak {gpu.TemperatureMax:0} °C, limit {gpu.TemperatureLimit} °C."));
        }
        else if (gpu.TemperatureMax >= gpu.TemperatureLimit - NearLimit)
        {
            findings.Add(new Finding(Severity.Warning, "gpu", "The GPU is at its boost temperature target, so clocks are being trimmed to hold it.",
                $"Peak {gpu.TemperatureMax:0} °C, within {NearLimit:0} °C of the {gpu.TemperatureLimit} °C target."));
        }
        if (brake >= 0.02)
        {
            findings.Add(new Finding(Severity.Warning, "gpu", "The GPU triggered a hardware slowdown or power brake.",
                $"Seen in {brake:P0} of samples; this is usually the laptop's power delivery or a thermal protection, not normal boosting."));
        }
        if (cap >= Often && busy >= Often)
        {
            var atCap = gpu.PowerMax is { } max ? $"; peak {max:0} W" : "";
            findings.Add(new Finding(Severity.Notice, "gpu", "The GPU is at its power cap under load, which is normal for a laptop GPU.",
                $"Power-cap throttling in {cap:P0} of samples{atCap}. Only more power headroom (a higher mode, AC power) or a lower load changes this."));
        }
    }

    private static void Fans(List<Finding> findings, IReadOnlyList<Telemetry> samples, ChipSummary cpu, ChipSummary? gpu, IReadOnlyList<FanSummary> fans)
    {
        var flatOut = fans.Where(f => f.FractionAtFullBoost >= 0.5).Select(f => f.Fan).ToList();
        var hotAnyway = cpu.TemperatureMax >= cpu.TemperatureLimit - 8 || (gpu?.TemperatureMax >= gpu?.TemperatureLimit - 8);
        if (flatOut.Count > 0 && hotAnyway)
        {
            findings.Add(new Finding(Severity.Warning, "fans", "The fans are already at full speed and the chips are still near their limits: cooling is saturated.",
                $"{string.Join(" and ", flatOut)} fan at full boost for most of the window. Check the vents and dust (Dust Defender), a cooling pad, or a lower power mode."));
        }
        else if (flatOut.Count > 0)
        {
            findings.Add(new Finding(Severity.Info, "fans", "The fans ran at full boost, with temperature headroom left.",
                $"{string.Join(" and ", flatOut)} at full boost for most of the window."));
        }
        if (samples.Any(s => s.FanLock is not null))
        {
            var lockName = samples.Last(s => s.FanLock is not null).FanLock;
            findings.Add(new Finding(Severity.Notice, "fans", "OpenSense's fan control was locked by the firmware or mode.", $"Reason: {lockName}."));
        }
        if (samples.Any(s => s.Failsafe))
            findings.Add(new Finding(Severity.Warning, "fans", "The fan failsafe handed the fans back to the firmware (no CPU temperature reading).", "Check that the PawnIO driver is installed."));
    }

    private static void Mode(List<Finding> findings, IReadOnlyList<Telemetry> samples, Telemetry last, bool loaded)
    {
        if (!loaded)
            return;
        if (last.OperatingMode is OperatingMode.Quiet or OperatingMode.Eco)
        {
            findings.Add(new Finding(Severity.Notice, "mode", $"The laptop is in {last.OperatingMode} mode, which caps performance.",
                "Balanced, Performance or Turbo raise the power limits (Turbo and Performance need AC power)."));
        }
        if (!last.OnAcPower)
        {
            findings.Add(new Finding(Severity.Notice, "power", "The laptop is on battery, which limits CPU and GPU power.", "Plug in the adapter for full performance."));
        }
        else if (last.PowerLimit != OpenSense.Core.Control.PowerLimit.None)
        {
            findings.Add(new Finding(Severity.Notice, "power", "The adapter or battery state is holding the operating modes back.", $"Power limit state: {last.PowerLimit}."));
        }
    }

    private static (string Verdict, string Headline) Decide(List<Finding> findings, bool loaded)
    {
        if (findings.Any(f => f.Severity == Severity.Critical) || findings.Any(f => f.Severity == Severity.Warning && f.Area is "cpu" or "gpu" or "fans"))
            return ("thermal-limited", "Hitting the thermal wall: a chip is at or near its temperature limit.");
        if (!loaded)
            return ("not-enough-load", "No sustained load in this window, so it says little about limits.");
        if (findings.Any(f => f.Summary.Contains("power limit", StringComparison.OrdinalIgnoreCase) || f.Summary.Contains("power cap", StringComparison.OrdinalIgnoreCase) || f.Area == "mode"))
            return ("power-limited", "Not thermally limited; performance is held by a power limit or the operating mode.");
        if (findings.Any(f => f.Severity == Severity.Notice))
            return ("warm", "Healthy but warm: there is some headroom left.");
        return ("healthy", "Healthy: temperatures and power are well inside their limits.");
    }

    private static ChipSummary Summarise(string name, IReadOnlyList<Telemetry> samples, int limit, Func<Telemetry, double?> temperature,
        Func<Telemetry, double?> load, Func<Telemetry, double?> power, Func<Telemetry, double?> clock)
    {
        List<double> temps = [.. samples.Select(temperature).OfType<double>()];
        List<double> loads = [.. samples.Select(load).OfType<double>()];
        List<double> powers = [.. samples.Select(power).OfType<double>()];
        List<double> clocks = [.. samples.Select(clock).OfType<double>()];
        return new ChipSummary(name, Avg(temps), Max(temps), limit, Avg(loads), Avg(powers), Max(powers), Avg(clocks), Max(clocks), Trend(samples, temperature));
    }

    private static ChipSummary Empty(string name) => new(name, null, null, null, null, null, null, null, null, null);

    private static List<FanSummary> SummariseFans(IReadOnlyList<Telemetry> samples)
    {
        List<FanSummary> result = [];
        foreach (var id in samples.SelectMany(s => s.Fans.Select(f => f.Id)).Distinct())
        {
            var readings = samples.Select(s => s.Fan(id)).OfType<FanTelemetry>().ToList();
            var rpms = readings.Select(r => r.Rpm).OfType<int>().ToList();
            var full = readings.Count(r => r.Behavior == FanBehavior.Max || r.BoostPercent >= 100);
            result.Add(new FanSummary(id.ToString(), rpms.Count > 0 ? Math.Round(rpms.Average()) : null, rpms.Count > 0 ? rpms.Max() : null,
                readings.Count > 0 ? Math.Round((double)full / readings.Count, 2) : 0));
        }
        return result;
    }

    private static double Fraction(IReadOnlyList<Telemetry> samples, Func<Telemetry, bool> test) =>
        samples.Count == 0 ? 0 : (double)samples.Count(test) / samples.Count;

    private static double? Avg(List<double> values) => values.Count > 0 ? Math.Round(values.Average(), 1) : null;

    private static double? Max(List<double> values) => values.Count > 0 ? Math.Round(values.Max(), 1) : null;

    /// <summary>Least-squares slope of the temperature, in °C per minute.</summary>
    private static double? Trend(IReadOnlyList<Telemetry> samples, Func<Telemetry, double?> temperature)
    {
        var points = samples.Select(s => (Seconds: (s.Timestamp - samples[0].Timestamp).TotalSeconds, Value: temperature(s))).Where(p => p.Value is not null).ToList();
        if (points.Count < 3)
            return null;
        var meanX = points.Average(p => p.Seconds);
        var meanY = points.Average(p => p.Value!.Value);
        var denominator = points.Sum(p => (p.Seconds - meanX) * (p.Seconds - meanX));
        return denominator < 1e-9 ? null : Math.Round(points.Sum(p => (p.Seconds - meanX) * (p.Value!.Value - meanY)) / denominator * 60, 1);
    }

    /// <summary>How much average power fell between the first and last third of the loaded samples (0.2 = 20% lower); null with too few.</summary>
    private static double? PowerFade(IReadOnlyList<Telemetry> samples, Func<Telemetry, double?> load, Func<Telemetry, double?> power)
    {
        var loaded = samples.Where(s => load(s) >= Busy && power(s) is not null).Select(s => power(s)!.Value).ToList();
        if (loaded.Count < 9)
            return null;
        var third = loaded.Count / 3;
        var first = loaded.Take(third).Average();
        var end = loaded.Skip(loaded.Count - third).Average();
        return first > 1 ? (first - end) / first : null;
    }
}

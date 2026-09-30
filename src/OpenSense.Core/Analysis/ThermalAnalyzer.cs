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
/// <param name="Code">A stable short name for the kind of finding, for programs: thermal-sustained, thermal-spike, near-limit, hot, ...</param>
/// <param name="Summary">What was seen, in a sentence.</param>
/// <param name="Evidence">The numbers behind it.</param>
public sealed record Finding(Severity Severity, string Area, string Code, string Summary, string Evidence);

/// <summary>
/// One chip over the window. A peak alone misleads (a shader compile or a loading screen can spike the CPU for seconds),
/// so this says where the chip <em>sits</em>: median and 95th percentile, how long it spent near its limit, and what it
/// did while it was actually loaded.
/// </summary>
/// <param name="TemperatureBands">Share of the window spent in each band of distance under the temperature limit.</param>
/// <param name="SecondsNearLimit">Total time within 3 °C of the limit.</param>
/// <param name="LongestNearLimitSeconds">The longest unbroken stretch within 3 °C of the limit.</param>
/// <param name="LoadedPowerMedianW">Median power over the samples where this chip's load was 60% or more.</param>
/// <param name="LoadedClockMedianMhz">Median clock over the same samples.</param>
public sealed record ChipSummary(
    string Name,
    double? TemperatureMedian,
    double? Temperature95th,
    double? TemperatureAvg,
    double? TemperatureMax,
    int? TemperatureLimit,
    double SecondsNearLimit,
    double LongestNearLimitSeconds,
    IReadOnlyDictionary<string, double> TemperatureBands,
    double? LoadMedian,
    double? PowerMedianW,
    double? Power95thW,
    double? PowerMaxW,
    double? LoadedPowerMedianW,
    double? ClockMedianMhz,
    double? ClockMaxMhz,
    double? LoadedClockMedianMhz,
    double? TemperatureTrendPerMinute);

public sealed record FanSummary(string Fan, double? RpmMedian, int? RpmMax, double FractionAtFullBoost);

/// <summary>The run split into equal stretches, so a change over time (heat soak, boost fading, a spike) shows up.</summary>
public sealed record TimelineBucket(
    double StartSeconds,
    double EndSeconds,
    double? CpuTemperatureC,
    double? CpuTemperatureMaxC,
    double? CpuLoad,
    double? CpuPowerW,
    double? CpuClockMhz,
    double? GpuTemperatureC,
    double? GpuTemperatureMaxC,
    double? GpuLoad,
    double? GpuPowerW,
    double? GpuClockMhz,
    double? CpuFanRpm,
    double? GpuFanRpm);

/// <summary>One unbroken stretch where a chip was being held back.</summary>
/// <param name="Kind">thermal, power-limit, current-limit or power-brake.</param>
public sealed record ThrottleEpisode(string Chip, string Kind, double StartSeconds, double DurationSeconds);

/// <param name="Verdict">healthy, warm, running-hot, power-limited, thermal-limited, not-enough-load or not-enough-data.</param>
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
    double? TotalPowerMedianW,
    double? TotalPowerMaxW,
    IReadOnlyList<TimelineBucket> Timeline,
    IReadOnlyList<ThrottleEpisode> ThrottleEpisodes,
    IReadOnlyList<Finding> Findings);

/// <summary>
/// Reads a run of telemetry the way someone troubleshooting a laptop would: is a chip stuck at its thermal limit, is
/// something capping its power, are the fans already flat out, or was it only a short spike. Pure, so it is testable
/// and an AI agent only has to interpret it.
/// </summary>
public static class ThermalAnalyzer
{
    public const int MinimumSamples = 5;
    private const double Busy = 60;              // percent load that counts as a real workload
    private const double NearLimit = 3;          // degrees under a limit that count as "at the wall"
    private const double Often = 0.25;           // share of samples that makes a power-limit flag a pattern
    private const double SustainedShare = 0.10;  // share of the window that makes a thermal problem sustained...
    private const double SustainedSeconds = 10;  // ...or an unbroken stretch this long
    private const int TimelineBuckets = 8;
    private const int MaximumEpisodes = 12;

    private static readonly string[] BandNames = ["30+ under limit", "20-30 under", "10-20 under", "3-10 under", "within 3 of limit or over"];

    public static ThermalReport Analyze(IReadOnlyList<Telemetry> samples)
    {
        if (samples.Count < MinimumSamples)
        {
            return new ThermalReport("not-enough-data", $"Fewer than {MinimumSamples} samples; wait a few seconds and ask again.", samples.Count, 0,
                null, null, Empty("CPU"), null, [], null, null, [], [], []);
        }

        var last = samples[^1];
        var interval = Interval(samples);
        var window = (last.Timestamp - samples[0].Timestamp).TotalSeconds + interval;
        var findings = new List<Finding>();
        var limits = last.Limits;

        var cpu = Summarise("CPU", samples, interval, limits.Cpu, s => s.CpuTemperature, s => s.CpuLoad, s => s.CpuPower, s => s.CpuClockMhz);
        var hasGpu = samples.Any(s => s.GpuTemperature is not null || s.GpuPower is not null);
        var gpu = hasGpu
            ? Summarise("GPU", samples, interval, limits.Gpu, s => s.GpuTemperature, s => s.GpuLoad, s => s.GpuPower, s => s.GpuClockMhz)
            : null;
        var fans = SummariseFans(samples);
        List<double> total = [.. samples.Select(s => s.TotalPower).OfType<double>()];

        var busyCpu = Fraction(samples, s => s.CpuLoad >= Busy);
        var busyGpu = Fraction(samples, s => s.GpuLoad >= Busy);
        var loaded = busyCpu >= Often || busyGpu >= Often;

        var episodes = new List<ThrottleEpisode>();
        Cpu(findings, episodes, samples, interval, window, cpu, last, busyCpu);
        if (gpu is not null)
            Gpu(findings, episodes, samples, interval, window, gpu, busyGpu);
        Fans(findings, samples, cpu, gpu, fans);
        Mode(findings, last, loaded);

        if (!loaded)
        {
            findings.Add(new Finding(Severity.Info, "data", "no-load", "The window has no sustained heavy load, so limits were not exercised.",
                $"CPU load was 60%+ in {busyCpu:P0} of samples and GPU load in {busyGpu:P0}. Run the game or benchmark, then analyse again."));
        }

        var (verdict, headline) = Decide(findings, loaded, cpu, gpu);
        return new ThermalReport(verdict, headline, samples.Count, Math.Round(window), last.OperatingMode?.ToString(), last.OnAcPower, cpu, gpu, fans,
            Median(total), total.Count > 0 ? Math.Round(total.Max(), 1) : null, Timeline(samples), [.. episodes.OrderBy(e => e.StartSeconds).Take(MaximumEpisodes)], findings);
    }

    private static void Cpu(List<Finding> findings, List<ThrottleEpisode> episodes, IReadOnlyList<Telemetry> samples, double interval, double window,
        ChipSummary cpu, Telemetry last, double busy)
    {
        const CpuThrottleReason Thermal = CpuThrottleReason.Thermal | CpuThrottleReason.Prochot | CpuThrottleReason.Critical;
        var thermalRuns = Runs(samples, interval, s => (s.CpuThrottle & Thermal) != 0);
        var powerRuns = Runs(samples, interval, s => (s.CpuThrottle & CpuThrottleReason.PowerLimit) != 0);
        var currentRuns = Runs(samples, interval, s => (s.CpuThrottle & CpuThrottleReason.CurrentLimit) != 0);
        AddEpisodes(episodes, "CPU", "thermal", thermalRuns);
        AddEpisodes(episodes, "CPU", "power-limit", powerRuns);
        AddEpisodes(episodes, "CPU", "current-limit", currentRuns);

        var thermalSeconds = thermalRuns.Sum(r => r.Seconds);
        var thermalLongest = thermalRuns.Count > 0 ? thermalRuns.Max(r => r.Seconds) : 0;
        var limit = cpu.TemperatureLimit ?? ThermalLimits.IntelCpuDefault;

        if (samples.Any(s => (s.CpuThrottle & CpuThrottleReason.Critical) != 0))
        {
            findings.Add(new Finding(Severity.Critical, "cpu", "critical", "The CPU reached its critical temperature.",
                $"Peak {cpu.TemperatureMax:0} °C against a throttle point of {limit} °C."));
        }
        if (IsSustained(thermalSeconds, thermalLongest, window))
        {
            findings.Add(new Finding(Severity.Warning, "cpu", "thermal-sustained", "The CPU is thermally throttling (at its temperature limit or PROCHOT).",
                $"The package reported thermal throttling in {thermalSeconds / window:P0} of the window over {thermalRuns.Count} stretch(es), the longest {thermalLongest:0} s; " +
                $"it sits at {cpu.TemperatureMedian:0} °C (95th percentile {cpu.Temperature95th:0} °C), limit {limit} °C."));
        }
        else if (IsSustained(cpu.SecondsNearLimit, cpu.LongestNearLimitSeconds, window))
        {
            findings.Add(new Finding(Severity.Warning, "cpu", "near-limit", "The CPU spends long stretches at its throttle point.",
                $"Within {NearLimit:0} °C of the {limit} °C limit for {cpu.SecondsNearLimit:0} s ({cpu.SecondsNearLimit / window:P0} of the window), the longest unbroken {cpu.LongestNearLimitSeconds:0} s; median {cpu.TemperatureMedian:0} °C."));
        }
        else if (thermalRuns.Count > 0 || cpu.SecondsNearLimit > 0)
        {
            var seconds = Math.Max(thermalSeconds, cpu.SecondsNearLimit);
            findings.Add(new Finding(Severity.Notice, "cpu", "thermal-spike", "The CPU touched its limit only briefly, which fits a short burst such as shader compilation or a loading screen.",
                $"Peak {cpu.TemperatureMax:0} °C for about {seconds:0} s in total (longest {Math.Max(thermalLongest, cpu.LongestNearLimitSeconds):0} s) of a {window:0} s window; it otherwise sits at {cpu.TemperatureMedian:0} °C (95th percentile {cpu.Temperature95th:0} °C)."));
        }
        else if (cpu.TemperatureMedian >= limit - 10)
        {
            findings.Add(new Finding(Severity.Notice, "cpu", "hot", "The CPU sits hot, close under its limit, without throttling for long.",
                $"Median {cpu.TemperatureMedian:0} °C, 95th percentile {cpu.Temperature95th:0} °C, limit {limit} °C: {limit - cpu.TemperatureMedian:0} °C of headroom at the typical moment."));
        }
        else if (cpu.Temperature95th >= limit - 10)
        {
            findings.Add(new Finding(Severity.Notice, "cpu", "hot-bursts", "The CPU gets hot in bursts but usually has room.",
                $"Median {cpu.TemperatureMedian:0} °C, but the 95th percentile is {cpu.Temperature95th:0} °C against a {limit} °C limit."));
        }

        var power = powerRuns.Sum(r => r.Seconds) / window;
        if (power >= Often)
        {
            var limitsText = last.CpuPowerLimit1 is { } pl1 ? $" (PL1 {pl1:0} W" + (last.CpuPowerLimit2 is { } pl2 ? $", PL2 {pl2:0} W)" : ")") : "";
            var belowPl1 = last.CpuPowerLimit1 is { } p1 && (cpu.LoadedPowerMedianW ?? cpu.PowerMedianW) is { } typical && typical < p1 * 0.8;
            var hot = cpu.TemperatureMedian >= limit - 10;
            var advice = belowPl1 && hot
                ? "Power sits well below PL1, so the limit in force is lower than PL1: the firmware is most likely cutting power to manage heat, which points at cooling rather than at the mode."
                : belowPl1
                    ? "Power sits well below PL1, so the limit in force is lower than PL1 (set dynamically by the firmware)."
                    : "If the temperature has room, a higher operating mode may raise the limit.";
            findings.Add(new Finding(Severity.Notice, "cpu", "power-limit", "A power limit is holding the CPU's clocks down.",
                $"Power-limit throttling in {power:P0} of the window{limitsText}; typical power {cpu.LoadedPowerMedianW ?? cpu.PowerMedianW:0} W. {advice}"));
        }
        var current = currentRuns.Sum(r => r.Seconds) / window;
        if (current >= Often)
            findings.Add(new Finding(Severity.Notice, "cpu", "current-limit", "The CPU hit its current limit (ICCmax).", $"Current-limit throttling in {current:P0} of the window."));

        if (busy >= Often && PowerFade(samples, s => s.CpuLoad, s => s.CpuPower) is { } fade and >= 0.15)
        {
            findings.Add(new Finding(Severity.Notice, "cpu", "boost-fade", "CPU power fell during the load (boost fading).",
                $"Power dropped {fade:P0} from the first third of the loaded samples to the last third; typical of PL2 giving way to PL1 or of heat soak."));
        }
    }

    private static void Gpu(List<Finding> findings, List<ThrottleEpisode> episodes, IReadOnlyList<Telemetry> samples, double interval, double window,
        ChipSummary gpu, double busy)
    {
        const ulong Thermal = GpuThrottle.SoftwareThermalSlowdown | GpuThrottle.HardwareThermalSlowdown;
        const ulong Brake = GpuThrottle.HardwarePowerBrake | GpuThrottle.HardwareSlowdown;
        var thermalRuns = Runs(samples, interval, s => s.GpuThrottleReasons is { } r && (r & Thermal) != 0);
        var brakeRuns = Runs(samples, interval, s => s.GpuThrottleReasons is { } r && (r & Brake) != 0);
        var capRuns = Runs(samples, interval, s => s.GpuThrottleReasons is { } r && (r & GpuThrottle.SoftwarePowerCap) != 0);
        AddEpisodes(episodes, "GPU", "thermal", thermalRuns);
        AddEpisodes(episodes, "GPU", "power-brake", brakeRuns);
        AddEpisodes(episodes, "GPU", "power-limit", capRuns);

        var thermalSeconds = thermalRuns.Sum(r => r.Seconds);
        var thermalLongest = thermalRuns.Count > 0 ? thermalRuns.Max(r => r.Seconds) : 0;
        var limit = gpu.TemperatureLimit ?? ThermalLimits.GpuDefault;

        if (IsSustained(thermalSeconds, thermalLongest, window))
        {
            findings.Add(new Finding(Severity.Warning, "gpu", "thermal-sustained", "The GPU is thermally throttling.",
                $"The driver reported a thermal slowdown in {thermalSeconds / window:P0} of the window, the longest stretch {thermalLongest:0} s; it sits at {gpu.TemperatureMedian:0} °C (95th percentile {gpu.Temperature95th:0} °C), limit {limit} °C."));
        }
        else if (IsSustained(gpu.SecondsNearLimit, gpu.LongestNearLimitSeconds, window))
        {
            findings.Add(new Finding(Severity.Warning, "gpu", "near-limit", "The GPU sits at its boost temperature target, so its clocks are being trimmed to hold it.",
                $"Within {NearLimit:0} °C of the {limit} °C target for {gpu.SecondsNearLimit:0} s ({gpu.SecondsNearLimit / window:P0} of the window); median {gpu.TemperatureMedian:0} °C."));
        }
        else if (thermalRuns.Count > 0 || gpu.SecondsNearLimit > 0)
        {
            findings.Add(new Finding(Severity.Notice, "gpu", "thermal-spike", "The GPU touched its temperature target only briefly.",
                $"Peak {gpu.TemperatureMax:0} °C for about {Math.Max(thermalSeconds, gpu.SecondsNearLimit):0} s of a {window:0} s window; it otherwise sits at {gpu.TemperatureMedian:0} °C."));
        }
        else if (gpu.TemperatureMedian >= limit - 8)
        {
            findings.Add(new Finding(Severity.Notice, "gpu", "hot", "The GPU sits close under its temperature target.",
                $"Median {gpu.TemperatureMedian:0} °C against a {limit} °C target."));
        }

        var brake = brakeRuns.Sum(r => r.Seconds) / window;
        if (brake >= 0.02)
        {
            findings.Add(new Finding(Severity.Warning, "gpu", "power-brake", "The GPU triggered a hardware slowdown or power brake.",
                $"Seen in {brake:P0} of the window over {brakeRuns.Count} stretch(es); this is usually the laptop's power delivery or a thermal protection, not normal boosting."));
        }
        var cap = capRuns.Sum(r => r.Seconds) / window;
        if (cap >= Often && busy >= Often)
        {
            var atCap = gpu.PowerMaxW is { } max ? $"; typical {gpu.LoadedPowerMedianW ?? gpu.PowerMedianW:0} W, peak {max:0} W" : "";
            findings.Add(new Finding(Severity.Notice, "gpu", "power-cap", "The GPU is at its power cap under load, which is normal for a laptop GPU.",
                $"Power-cap throttling in {cap:P0} of the window{atCap}. Only more power headroom (a higher mode, AC power) or a lower load changes this."));
        }
    }

    private static void Fans(List<Finding> findings, IReadOnlyList<Telemetry> samples, ChipSummary cpu, ChipSummary? gpu, IReadOnlyList<FanSummary> fans)
    {
        var flatOut = fans.Where(f => f.FractionAtFullBoost >= 0.5).Select(f => f.Fan).ToList();
        var hotAnyway = cpu.TemperatureMedian >= cpu.TemperatureLimit - 8 || (gpu?.TemperatureMedian >= gpu?.TemperatureLimit - 8);
        if (flatOut.Count > 0 && hotAnyway)
        {
            findings.Add(new Finding(Severity.Warning, "fans", "fans-saturated", "The fans are already at full speed and the chips still sit near their limits: cooling is saturated.",
                $"{string.Join(" and ", flatOut)} fan at full boost for most of the window. Check the vents and dust (Dust Defender), a cooling pad, or a lower power mode."));
        }
        else if (flatOut.Count > 0)
        {
            findings.Add(new Finding(Severity.Info, "fans", "fans-full", "The fans ran at full boost, with temperature headroom left.",
                $"{string.Join(" and ", flatOut)} at full boost for most of the window."));
        }
        if (samples.Any(s => s.FanLock is not null))
        {
            var lockName = samples.Last(s => s.FanLock is not null).FanLock;
            findings.Add(new Finding(Severity.Notice, "fans", "fan-lock", "OpenSense's fan control was locked by the firmware or mode.", $"Reason: {lockName}."));
        }
        if (samples.Any(s => s.Failsafe))
        {
            findings.Add(new Finding(Severity.Warning, "fans", "failsafe", "The fan failsafe handed the fans back to the firmware (no CPU temperature reading).",
                "Check that the PawnIO driver is installed."));
        }
    }

    private static void Mode(List<Finding> findings, Telemetry last, bool loaded)
    {
        if (!loaded)
            return;
        if (last.OperatingMode is OperatingMode.Quiet or OperatingMode.Eco)
        {
            findings.Add(new Finding(Severity.Notice, "mode", "mode", $"The laptop is in {last.OperatingMode} mode, which caps performance.",
                "Balanced, Performance or Turbo raise the power limits (Turbo and Performance need AC power)."));
        }
        if (!last.OnAcPower)
            findings.Add(new Finding(Severity.Notice, "power", "battery", "The laptop is on battery, which limits CPU and GPU power.", "Plug in the adapter for full performance."));
        else if (last.PowerLimit != PowerLimit.None)
            findings.Add(new Finding(Severity.Notice, "power", "supply", "The adapter or battery state is holding the operating modes back.", $"Power limit state: {last.PowerLimit}."));
    }

    private static (string Verdict, string Headline) Decide(List<Finding> findings, bool loaded, ChipSummary cpu, ChipSummary? gpu)
    {
        if (findings.Any(f => f.Severity == Severity.Critical || (f.Severity == Severity.Warning && f.Area is "cpu" or "gpu" or "fans")))
            return ("thermal-limited", "Hitting the thermal wall: a chip is at or near its temperature limit for a sustained time.");
        if (!loaded)
            return ("not-enough-load", "No sustained load in this window, so it says little about limits.");
        if (findings.Any(f => f.Code is "hot"))
            return ("running-hot", "Running hot, close under the limit at the typical moment, but not throttling for long.");
        if (findings.Any(f => f.Code is "power-limit" or "power-cap" or "mode" or "battery" or "supply"))
            return ("power-limited", "Not thermally limited for long; performance is held by a power limit or the operating mode.");
        if (findings.Any(f => f.Severity == Severity.Notice))
            return ("warm", "Healthy: any heat is brief or has headroom.");
        return ("healthy", "Healthy: temperatures and power sit well inside their limits.");
    }

    /// <summary>A problem counts as sustained when it fills a tenth of the window or one unbroken stretch is long.</summary>
    private static bool IsSustained(double totalSeconds, double longestSeconds, double window) =>
        totalSeconds > 0 && (totalSeconds / window >= SustainedShare || longestSeconds >= SustainedSeconds);

    private static ChipSummary Summarise(string name, IReadOnlyList<Telemetry> samples, double interval, int limit, Func<Telemetry, double?> temperature,
        Func<Telemetry, double?> load, Func<Telemetry, double?> power, Func<Telemetry, double?> clock)
    {
        List<double> temps = [.. samples.Select(temperature).OfType<double>()];
        List<double> loads = [.. samples.Select(load).OfType<double>()];
        List<double> powers = [.. samples.Select(power).OfType<double>()];
        List<double> clocks = [.. samples.Select(clock).OfType<double>()];
        List<double> loadedPowers = [.. samples.Where(s => load(s) >= Busy).Select(power).OfType<double>()];
        List<double> loadedClocks = [.. samples.Where(s => load(s) >= Busy).Select(clock).OfType<double>()];
        var near = Runs(samples, interval, s => temperature(s) >= limit - NearLimit);

        return new ChipSummary(name, Median(temps), Percentile(temps, 0.95), Avg(temps), temps.Count > 0 ? Math.Round(temps.Max(), 1) : null, limit,
            Math.Round(near.Sum(r => r.Seconds), 1), Math.Round(near.Count > 0 ? near.Max(r => r.Seconds) : 0, 1), Bands(temps, limit),
            Median(loads), Median(powers), Percentile(powers, 0.95), powers.Count > 0 ? Math.Round(powers.Max(), 1) : null, Median(loadedPowers),
            Median(clocks), clocks.Count > 0 ? Math.Round(clocks.Max()) : null, Median(loadedClocks), Trend(samples, temperature));
    }

    private static ChipSummary Empty(string name) =>
        new(name, null, null, null, null, null, 0, 0, new Dictionary<string, double>(), null, null, null, null, null, null, null, null, null);

    private static Dictionary<string, double> Bands(List<double> temps, int limit)
    {
        var counts = new int[BandNames.Length];
        foreach (var t in temps)
        {
            var under = limit - t;
            counts[under >= 30 ? 0 : under >= 20 ? 1 : under >= 10 ? 2 : under > NearLimit ? 3 : 4]++;
        }
        return BandNames.Select((n, i) => (n, share: temps.Count > 0 ? Math.Round((double)counts[i] / temps.Count, 3) : 0)).ToDictionary(b => b.n, b => b.share);
    }

    private static List<TimelineBucket> Timeline(IReadOnlyList<Telemetry> samples)
    {
        var start = samples[0].Timestamp;
        var count = Math.Min(TimelineBuckets, samples.Count / 2);
        List<TimelineBucket> buckets = [];
        for (var b = 0; b < count; b++)
        {
            var from = b * samples.Count / count;
            var to = (b + 1) * samples.Count / count;
            var slice = samples.Skip(from).Take(to - from).ToList();
            if (slice.Count == 0)
                continue;
            buckets.Add(new TimelineBucket(
                Math.Round((slice[0].Timestamp - start).TotalSeconds), Math.Round((slice[^1].Timestamp - start).TotalSeconds),
                AvgOf(slice, s => s.CpuTemperature), MaxOf(slice, s => s.CpuTemperature), AvgOf(slice, s => s.CpuLoad), AvgOf(slice, s => s.CpuPower), AvgOf(slice, s => s.CpuClockMhz),
                AvgOf(slice, s => s.GpuTemperature), MaxOf(slice, s => s.GpuTemperature), AvgOf(slice, s => s.GpuLoad), AvgOf(slice, s => s.GpuPower), AvgOf(slice, s => s.GpuClockMhz),
                AvgOfInt(slice, s => s.Fan(FanId.Cpu)?.Rpm), AvgOfInt(slice, s => s.Fan(FanId.Gpu)?.Rpm)));
        }
        return buckets;
    }

    private static List<FanSummary> SummariseFans(IReadOnlyList<Telemetry> samples)
    {
        List<FanSummary> result = [];
        foreach (var id in samples.SelectMany(s => s.Fans.Select(f => f.Id)).Distinct())
        {
            var readings = samples.Select(s => s.Fan(id)).OfType<FanTelemetry>().ToList();
            List<double> rpms = [.. readings.Select(r => r.Rpm).OfType<int>().Select(r => (double)r)];
            var full = readings.Count(r => r.Behavior == FanBehavior.Max || r.BoostPercent >= 100);
            result.Add(new FanSummary(id.ToString(), Median(rpms) is { } m ? Math.Round(m) : null, rpms.Count > 0 ? (int)rpms.Max() : null,
                readings.Count > 0 ? Math.Round((double)full / readings.Count, 2) : 0));
        }
        return result;
    }

    private readonly record struct Run(int Start, double StartSeconds, double Seconds);

    /// <summary>The unbroken stretches where <paramref name="test"/> held; a stretch lasts from its first sample to one interval past its last.</summary>
    private static List<Run> Runs(IReadOnlyList<Telemetry> samples, double interval, Func<Telemetry, bool> test)
    {
        List<Run> runs = [];
        int? begin = null;
        for (var i = 0; i <= samples.Count; i++)
        {
            var on = i < samples.Count && test(samples[i]);
            if (on && begin is null)
            {
                begin = i;
            }
            else if (!on && begin is { } from)
            {
                var seconds = (samples[i - 1].Timestamp - samples[from].Timestamp).TotalSeconds + interval;
                runs.Add(new Run(from, (samples[from].Timestamp - samples[0].Timestamp).TotalSeconds, seconds));
                begin = null;
            }
        }
        return runs;
    }

    private static void AddEpisodes(List<ThrottleEpisode> episodes, string chip, string kind, List<Run> runs) =>
        episodes.AddRange(runs.Select(r => new ThrottleEpisode(chip, kind, Math.Round(r.StartSeconds), Math.Round(r.Seconds, 1))));

    /// <summary>The usual gap between samples, so a one-sample stretch still counts as about a second.</summary>
    private static double Interval(IReadOnlyList<Telemetry> samples)
    {
        var gaps = Enumerable.Range(1, samples.Count - 1).Select(i => (samples[i].Timestamp - samples[i - 1].Timestamp).TotalSeconds).Where(g => g > 0).Order().ToList();
        return gaps.Count > 0 ? gaps[gaps.Count / 2] : 1;
    }

    private static double Fraction(IReadOnlyList<Telemetry> samples, Func<Telemetry, bool> test) =>
        samples.Count == 0 ? 0 : (double)samples.Count(test) / samples.Count;

    private static double? Avg(List<double> values) => values.Count > 0 ? Math.Round(values.Average(), 1) : null;

    private static double? Median(List<double> values) => Percentile(values, 0.5);

    private static double? Percentile(List<double> values, double p)
    {
        if (values.Count == 0)
            return null;
        var sorted = values.Order().ToList();
        var position = p * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return Math.Round(sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower), 1);
    }

    private static double? AvgOf(List<Telemetry> slice, Func<Telemetry, double?> value) => Avg([.. slice.Select(value).OfType<double>()]);

    private static double? AvgOfInt(List<Telemetry> slice, Func<Telemetry, int?> value) => Avg([.. slice.Select(value).Where(v => v is not null).Select(v => (double)v!.Value)]);

    private static double? MaxOf(List<Telemetry> slice, Func<Telemetry, double?> value)
    {
        List<double> values = [.. slice.Select(value).OfType<double>()];
        return values.Count > 0 ? Math.Round(values.Max(), 1) : null;
    }

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

    /// <summary>How much power fell between the first and last third of the loaded samples (0.2 = 20% lower); null with too few.</summary>
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

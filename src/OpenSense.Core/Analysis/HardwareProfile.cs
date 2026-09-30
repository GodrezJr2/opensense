using System.Text.RegularExpressions;

namespace OpenSense.Core.Analysis;

/// <summary>What is known about the machine, for <see cref="HardwareProfiler"/>: measured facts, and the names to classify.</summary>
public sealed record ProfileInput(
    string? Model,
    string? CpuName,
    string? GpuName,
    int? PhysicalCores,
    int? LogicalProcessors,
    double? CpuBasePowerW,
    double? CpuPl1W,
    double? CpuPl2W,
    double? GpuMaxPowerW,
    double? GpuDefaultPowerW,
    double? GpuMemoryGb,
    int FanCount,
    double? MemoryGb);

/// <summary>A usual range of temperatures for this class of chip under sustained load (a rule of thumb, not a measurement).</summary>
public sealed record TypicalRange(double LowC, double HighC);

/// <param name="Series">HX, H, HS, P or U (Intel and AMD mobile naming); null when the name isn't recognised.</param>
/// <param name="Class">What that series is built for, in words.</param>
/// <param name="TypicalSustainedTemperature">What temperatures are usual for the class under sustained load.</param>
public sealed record CpuProfile(
    string? Name,
    string? Vendor,
    string? Series,
    string? Generation,
    string Class,
    int? PhysicalCores,
    int? LogicalProcessors,
    double? BasePowerW,
    double? SustainedPowerLimitW,
    double? BoostPowerLimitW,
    TypicalRange? TypicalSustainedTemperature);

/// <param name="MaxPowerW">The most the driver lets the board draw: the laptop's graphics power (TGP), which differs between laptops with the same GPU.</param>
public sealed record GpuProfile(
    string? Name,
    string? Family,
    string? Tier,
    double? MaxPowerW,
    double? DefaultPowerW,
    double? MemoryGb,
    string? PowerNote,
    TypicalRange? TypicalSustainedTemperature);

public sealed record LaptopProfile(string? Model, string? Line, string? Tier, int FanCount, string? CoolingNote);

/// <param name="WorstCaseCombinedPowerW">The CPU's sustained limit plus the GPU's power limit: what both could draw together if nothing held them back.</param>
/// <param name="Caveat">How much of this is measured and how much is a rule of thumb.</param>
public sealed record HardwareProfile(CpuProfile Cpu, GpuProfile? Gpu, LaptopProfile Laptop, double? WorstCaseCombinedPowerW, string Caveat)
{
    /// <summary>The profile in a few plain sentences for a report.</summary>
    public IReadOnlyList<string> Describe()
    {
        List<string> lines = [];
        lines.Add($"CPU: {Cpu.Name ?? "unknown"}" + (Cpu.Series is null ? "" : $", {Cpu.Vendor} {Cpu.Series}-series") + $". {Cpu.Class}"
            + (Cpu.PhysicalCores is { } c ? $" {c} cores" + (Cpu.LogicalProcessors is { } l ? $" / {l} threads" : "") + "." : "")
            + (Cpu.BasePowerW is { } b ? $" Base power {b:0} W" + (Cpu.BoostPowerLimitW is { } p2 ? $", boost limit {p2:0} W." : ".") : ""));
        if (Gpu is not null)
        {
            lines.Add($"GPU: {Gpu.Name ?? "unknown"}" + (Gpu.Tier is null ? "" : $" ({Gpu.Tier} tier)") + (Gpu.MemoryGb is { } m ? $", {m:0.#} GB" : "") + "."
                + (Gpu.PowerNote is { } note ? " " + note : Gpu.MaxPowerW is { } w ? $" Power limit up to {w:0} W." : ""));
        }
        lines.Add($"Laptop: {Laptop.Model ?? "unknown model"}" + (Laptop.Line is null ? "" : $" is {Laptop.Line}" + (Laptop.Tier is null ? "" : $" ({Laptop.Tier})")) + $", {Laptop.FanCount} fan(s)."
            + (Laptop.CoolingNote is { } cool ? " " + cool : ""));
        return lines;
    }
}

/// <summary>
/// Turns names and a few measured limits into something an agent can judge readings against: what kind of CPU and GPU
/// this is and what laptop line it sits in. The power figures come from the hardware; the classes, tiers and usual
/// temperature ranges are rules of thumb from how each line is built, and are labelled as such.
/// </summary>
public static partial class HardwareProfiler
{
    private const string Caveat =
        "Core counts, base power, power limits, TGP and memory are read from the hardware. The class descriptions, tiers and " +
        "'typical' temperature ranges are rules of thumb for each product line, not measurements of this unit.";

    public static HardwareProfile Describe(ProfileInput input)
    {
        var cpu = Cpu(input);
        var gpu = input.GpuName is null && input.GpuMaxPowerW is null ? null : Gpu(input);
        var laptop = Laptop(input);
        double? worst = cpu.SustainedPowerLimitW is { } pl1 && gpu?.MaxPowerW is { } tgp ? pl1 + tgp : null;
        return new HardwareProfile(cpu, gpu, laptop, worst, Caveat);
    }

    private static CpuProfile Cpu(ProfileInput input)
    {
        var name = input.CpuName ?? "";
        string? vendor = null, series = null, generation = null;

        if (Ultra().Match(name) is { Success: true } ultra)
        {
            vendor = "Intel";
            series = Series(ultra.Groups[3].Value);
            generation = "Core Ultra";
        }
        else if (IntelCore().Match(name) is { Success: true } core)
        {
            vendor = "Intel";
            series = Series(core.Groups[3].Value);
            var model = core.Groups[2].Value;
            generation = (model.Length == 5 || (model.Length == 4 && model[0] == '1') ? model[..2] : model[..1]) + "th gen";
        }
        else if (Ryzen().Match(name) is { Success: true } ryzen)
        {
            vendor = "AMD";
            series = ryzen.Groups[3].Success ? Series(ryzen.Groups[3].Value) : null;
            generation = ryzen.Groups[2].Value[..1] + "000 series";
        }

        var (description, range) = series switch
        {
            "HX" => ("High-power performance class: desktop-derived silicon, around 55 W base and well over 100 W at boost. It runs hot by design; 80-95 C under sustained load is common in a laptop.", new TypicalRange(78, 92)),
            "H" => ("High-performance class, around 45 W base. Runs warm under sustained load.", new TypicalRange(75, 90)),
            "HS" or "P" => ("Balanced performance class, around 28-35 W base, meant for thinner laptops.", new TypicalRange(72, 88)),
            "U" => ("Efficiency class, 15-28 W: built for battery life and throttles sooner under sustained heavy load.", new TypicalRange(65, 85)),
            _ => ("Class unknown.", (TypicalRange?)null),
        };

        return new CpuProfile(input.CpuName, vendor, series, generation, description, input.PhysicalCores, input.LogicalProcessors, input.CpuBasePowerW,
            input.CpuPl1W, input.CpuPl2W, range);
    }

    private static string Series(string suffix) =>
        suffix.StartsWith("HX", StringComparison.Ordinal) ? "HX"
        : suffix.StartsWith("HS", StringComparison.Ordinal) ? "HS"
        : suffix.StartsWith('H') ? "H"
        : suffix.StartsWith('P') ? "P"
        : suffix.StartsWith('U') ? "U"
        : suffix;

    private static GpuProfile Gpu(ProfileInput input)
    {
        string? family = null, tier = null, note = null;
        var name = input.GpuName ?? "";
        if (Rtx().Match(name) is { Success: true } rtx && int.TryParse(rtx.Groups[1].Value, out var model))
        {
            var step = model % 100;
            family = $"GeForce RTX {model / 100}0-series" + (name.Contains("Laptop", StringComparison.OrdinalIgnoreCase) ? " Laptop" : "");
            tier = step switch { <= 50 => "entry", 60 => "mainstream", 70 => "upper-mainstream", 80 => "high-end", _ => "enthusiast" };
            if (model / 100 == 40 && input.GpuMaxPowerW is { } tgp)
            {
                var (low, high) = step <= 70 ? (35, 115) : step == 80 ? (60, 150) : (80, 150);
                note = $"Laptop RTX 40 cards of this model run {low}-{high} W depending on the laptop; this one allows up to {tgp:0} W"
                    + (tgp >= high - 10 ? ", the top of the range." : tgp <= low + 25 ? ", the low end of the range." : ".");
            }
        }
        return new GpuProfile(input.GpuName, family, tier, input.GpuMaxPowerW, input.GpuDefaultPowerW, input.GpuMemoryGb, note,
            family?.Contains("Laptop", StringComparison.Ordinal) == true ? new TypicalRange(75, 87) : null);
    }

    private static LaptopProfile Laptop(ProfileInput input)
    {
        var model = input.Model ?? "";
        var (line, tier, cooling) = model switch
        {
            _ when PredatorHeliosNeo().IsMatch(model) => ("Predator Helios Neo", "mainstream gaming", "Acer's mainstream gaming line: dual-fan cooling sized for a high-power CPU plus a mid-range GPU, with typically less thermal margin than the flagship Helios 16/18."),
            _ when PredatorHelios().IsMatch(model) => ("Predator Helios", "flagship gaming", "Acer's flagship large gaming chassis, with the most cooling in its gaming line."),
            _ when PredatorTriton().IsMatch(model) => ("Predator Triton", "premium thin gaming", "Slimmer chassis: powerful parts in less cooling, so sustained loads run warmer or throttle earlier."),
            _ when Nitro().IsMatch(model) => ("Nitro", "entry gaming", "Acer's value gaming line: cooling built to a price, so expect less margin under sustained load."),
            _ => ((string?)null, (string?)null, (string?)null),
        };
        return new LaptopProfile(input.Model, line, tier, input.FanCount, cooling);
    }

    [GeneratedRegex(@"Core\(TM\)\s+Ultra\s+([3579])\s+(\d{3})([A-Z]*)", RegexOptions.IgnoreCase)]
    private static partial Regex Ultra();

    [GeneratedRegex(@"Core\(TM\)\s+(i[3579])-(\d{4,5})([A-Z0-9]*)", RegexOptions.IgnoreCase)]
    private static partial Regex IntelCore();

    [GeneratedRegex(@"Ryzen\s+([3579])\s+(\d{4})(HX3D|HX|HS|H|U)?", RegexOptions.IgnoreCase)]
    private static partial Regex Ryzen();

    [GeneratedRegex(@"RTX\s+(\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex Rtx();

    [GeneratedRegex(@"\bPHN\d", RegexOptions.IgnoreCase)]
    private static partial Regex PredatorHeliosNeo();

    [GeneratedRegex(@"\bPH(3D)?\d", RegexOptions.IgnoreCase)]
    private static partial Regex PredatorHelios();

    [GeneratedRegex(@"\bPT\d", RegexOptions.IgnoreCase)]
    private static partial Regex PredatorTriton();

    [GeneratedRegex(@"\bAN\d", RegexOptions.IgnoreCase)]
    private static partial Regex Nitro();
}

using System.Management;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenSense.Core.Analysis;
using OpenSense.Core.Control;
using OpenSense.Core.Ipc;
using OpenSense.Core.Monitoring;

namespace OpenSense.Mcp;

/// <summary>
/// The tools an AI agent gets: what the machine is, how it is doing now and over the last minutes, and a ready-made
/// reading of whether it is thermally or power limited. Those only read. When the server was started with
/// <c>--allow-changes</c> there are also tools that set a performance profile or the CPU's power limits; those go
/// through the same clamps and gradual stepping as the app's own controls, and can be undone with <c>restore_firmware_power</c>.
/// </summary>
public sealed class AgentTools(IAgentSource source, bool allowChanges = false)
{
    public const string Instructions =
        "OpenSense reads a gaming laptop's sensors. Use machine_info once for what the hardware is, analyze_thermals for " +
        "'is it throttling / hitting the thermal wall / power limited', current_status for a live reading, and recent_history " +
        "for a time series. To diagnose a workload: have the user run it (for example in Turbo mode), then call analyze_thermals " +
        "with seconds covering the run. Read the 'context' lines it returns: they say what kind of CPU, GPU and laptop this is, " +
        "because what is normal differs a lot (an HX-series CPU in a mainstream chassis runs far hotter than a U-series one). " +
        "Class descriptions and 'typical' ranges are rules of thumb; core counts, power limits and memory are measured. Judge by where the chips SIT (median and 95th percentile, time near the limit, the " +
        "timeline and throttle episodes), not by the peak: a short spike to the limit is usually shader compilation or loading, " +
        "and the report separates it from a sustained problem. Terms: PL1 is the CPU's sustained power limit and PL2 its short boost limit, both set " +
        "by the operating mode; 'power-limited' means clocks are held by a power limit, 'thermal-limited' means by temperature; " +
        "a GPU 'power cap' under full load is normal on laptops. These tools are read-only and cannot change any setting.";

    public const string ChangeInstructions =
        " Changing tools are enabled: set_performance_profile picks silent, quiet_performance, optimize, turbo or unleash (the Acer operating " +
        "mode, the fans and the CPU's power together); set_cpu_power sets the CPU's PL1/PL2/window and a temperature ceiling (values are clamped " +
        "and applied in small steps while the CPU has temperature headroom); restore_firmware_power gives the limits back to the firmware. Measured on the " +
        "PHN16-72: PL1 and PL2 can be lowered below the firmware's 115/157 W but raising them has no lasting effect, and about 65 W keeps " +
        "84 % of full CPU speed at far lower fan noise. After a change, watch current_status (cpu.powerControl) and check the temperatures before " +
        "changing again. Tell the user what you changed.";

    /// <summary>What the server tells the agent about itself.</summary>
    public string InstructionsText => allowChanges
        ? Instructions.Replace("These tools are read-only and cannot change any setting.", "The reading tools are read-only.") + ChangeInstructions
        : Instructions;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static JsonArray Definitions() =>
    [
        Tool("machine_info",
            "What this laptop is: model, BIOS, CPU, GPU, memory, OS, which operating modes and fans it has, and the current OpenSense settings.",
            new JsonObject()),
        Tool("current_status",
            "The latest reading: CPU and GPU temperature, load, power draw (watts), clocks, PL1/PL2 limits, throttle reasons, fan RPM, operating mode and power source.",
            new JsonObject()),
        Tool("recent_history",
            "A time series of recent readings, evenly sampled, for spotting trends (temperature climbing, power or clocks dropping).",
            new JsonObject
            {
                ["seconds"] = Integer("How far back to look, in seconds (1-3600). Default 300.", 300),
                ["points"] = Integer("How many rows to return (2-240). Default 60.", 60),
            }),
        Tool("analyze_thermals",
            "Analyses a recent run: is the CPU or GPU at its thermal limit, is a power limit holding clocks down, are the fans saturated, is the operating mode or battery the cause. " +
            "Returns a verdict (healthy, warm, power-limited, thermal-limited, not-enough-load) with findings and their evidence.",
            new JsonObject { ["seconds"] = Integer("How far back the run to analyse started, in seconds (5-3600). Default 300.", 300) }),
    ];

    /// <summary>The reading tools, and the changing ones when they are allowed.</summary>
    public JsonArray AllDefinitions()
    {
        var all = Definitions();
        if (allowChanges)
        {
            foreach (var tool in ChangeDefinitions())
                all.Add(tool!.DeepClone());
        }
        return all;
    }

    private static JsonArray ChangeDefinitions() =>
    [
        ChangingTool("set_performance_profile",
            "Sets a whole-laptop performance profile: silent (Quiet mode, CPU 35 W), quiet_performance (Balanced, CPU about 65 W: 84 % speed at low fan noise), optimize (Performance, CPU about 80 W), " +
            "turbo (Turbo, the firmware's limits) or unleash (Turbo, fans at full speed). Leaves curves, GPU offsets and lighting alone.",
            new JsonObject
            {
                ["profile"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("silent", "quiet_performance", "optimize", "turbo", "unleash"),
                    ["description"] = "Which profile.",
                },
            }, required: "profile"),
        ChangingTool("set_cpu_power",
            "Sets the CPU's power: a preset (firmware, eco, quiet, optimize, turbo, unleash), or custom values with pl1_w (sustained watts, 15-157), pl2_w (burst watts, up to 200), window_s (8-128 s) and " +
            "ceiling_c (the temperature to keep under, 70-95; it can also be given with a preset). Values are clamped and applied in steps of 5 W every 4 s while the CPU has temperature headroom, backing off when it doesn't.",
            new JsonObject
            {
                ["preset"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("firmware", "eco", "quiet", "optimize", "turbo", "unleash"), ["description"] = "A preset." },
                ["pl1_w"] = new JsonObject { ["type"] = "number", ["description"] = "Custom sustained limit in watts." },
                ["pl2_w"] = new JsonObject { ["type"] = "number", ["description"] = "Custom burst limit in watts (default 1.3 x PL1)." },
                ["window_s"] = new JsonObject { ["type"] = "number", ["description"] = "Custom PL1 window in seconds (default 56)." },
                ["ceiling_c"] = new JsonObject { ["type"] = "integer", ["description"] = "Temperature target in Celsius." },
            }),
        ChangingTool("restore_firmware_power",
            "Gives the CPU's power limits back to the firmware (the values found before OpenSense changed them).", new JsonObject()),
    ];

    public async Task<JsonNode> CallAsync(string name, JsonObject? arguments)
    {
        object result = name switch
        {
            "set_performance_profile" => await SetPerformanceProfileAsync(arguments),
            "set_cpu_power" => await SetCpuPowerAsync(arguments),
            "restore_firmware_power" => await ChangeAsync(p => p with { CpuPower = new CpuPowerSettings() }, "The CPU's limits go back to the firmware's."),
            "machine_info" => await MachineInfoAsync(),
            "current_status" => Status(await source.SnapshotAsync()),
            "recent_history" => History(await source.HistoryAsync(Seconds(arguments, 300)), Clamp(arguments, "points", 60, 2, 240)),
            "analyze_thermals" => await AnalyzeAsync(Seconds(arguments, 300)),
            _ => throw new ToolException($"Unknown tool {name}."),
        };
        return JsonSerializer.SerializeToNode(result, Json)!;
    }

    private async Task<object> SetPerformanceProfileAsync(JsonObject? arguments)
    {
        var text = arguments?["profile"]?.GetValue<string>()?.Replace("_", "");
        if (!Enum.TryParse<PerformanceProfile>(text, ignoreCase: true, out var preset))
            throw new ToolException("profile must be one of silent, quiet_performance, optimize, turbo, unleash.");
        return await ChangeAsync(p => ProfilePresets.Apply(p, preset), $"Profile {preset} set.");
    }

    private async Task<object> SetCpuPowerAsync(JsonObject? arguments)
    {
        double? pl1 = Number(arguments, "pl1_w"), pl2 = Number(arguments, "pl2_w"), window = Number(arguments, "window_s"), ceiling = Number(arguments, "ceiling_c");
        int? ceilingC = ceiling is { } c ? (int)Math.Round(c) : null;
        CpuPowerSettings settings;
        if (arguments?["preset"]?.GetValue<string>() is { } presetName)
        {
            if (!Enum.TryParse<CpuPowerPreset>(presetName, ignoreCase: true, out var preset) || preset == CpuPowerPreset.Custom)
                throw new ToolException("preset must be one of firmware, eco, quiet, optimize, turbo, unleash (or give pl1_w for custom values).");
            if (pl1 is not null || pl2 is not null || window is not null)
                throw new ToolException("Give either a preset or custom values (pl1_w, pl2_w, window_s), not both; ceiling_c goes with either.");
            settings = new CpuPowerSettings { Preset = preset, CeilingC = ceilingC };
        }
        else if (pl1 is { } sustained)
        {
            settings = new CpuPowerSettings
            {
                Preset = CpuPowerPreset.Custom,
                Custom = new CpuPowerTarget(sustained, pl2 ?? sustained * 1.3, window ?? 56, ceilingC ?? 86),
            };
        }
        else
        {
            throw new ToolException("Give a preset, or pl1_w for custom values.");
        }
        return await ChangeAsync(p => p with { CpuPower = settings },
            "OpenSense moves the limits in small steps and backs off near the temperature target, so check current_status (cpu.powerControl) over the next minute.");
    }

    private async Task<object> ChangeAsync(Func<ControlProfile, ControlProfile> change, string note)
    {
        if (!allowChanges)
            throw new ToolException("This OpenSense server is read-only. Start it with --allow-changes (or OPENSENSE_MCP_ALLOW_CHANGES=1) to let an agent change settings.");
        var snapshot = await source.SnapshotAsync();
        var next = change(snapshot.Settings.Profile);
        await source.SetProfileAsync(next);
        var target = next.CpuPower.Target();
        return new
        {
            changed = true,
            note,
            now = new
            {
                operatingMode = next.OperatingMode,
                fanControl = next.Mode,
                cpuPower = new
                {
                    preset = next.CpuPower.Preset,
                    target = target is null ? null : new { pl1W = target.Pl1W, pl2W = target.Pl2W, windowSeconds = target.Pl1WindowS, ceilingC = target.CeilingC },
                },
            },
        };
    }

    private static double? Number(JsonObject? arguments, string name) =>
        arguments?[name] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    private async Task<object> AnalyzeAsync(int seconds)
    {
        var history = await source.HistoryAsync(seconds);
        EngineSnapshot? snapshot = null;
        try
        {
            snapshot = await source.SnapshotAsync();
        }
        catch (ToolException)
        {
            // The analysis still works without the machine's profile.
        }
        return ThermalAnalyzer.Analyze(history, snapshot is null ? null : Profile(snapshot));
    }

    /// <summary>What kind of machine this is: measured limits from the service plus the names, classified.</summary>
    public static HardwareProfile Profile(EngineSnapshot snapshot)
    {
        var t = snapshot.Latest;
        var (physical, logical) = CpuCores();
        return HardwareProfiler.Describe(new ProfileInput(
            snapshot.DeviceName, snapshot.CpuName, snapshot.GpuName, physical, logical ?? Environment.ProcessorCount,
            t?.CpuBasePowerW, t?.CpuPowerLimit1, t?.CpuPowerLimit2, t?.GpuPowerLimitMax, t?.GpuPowerLimitDefault, t?.GpuMemoryGb,
            snapshot.Capabilities.Fans.Count, Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024 / 1024, 1), t?.GpuPowerLimit));
    }

    private static (int? Physical, int? Logical) CpuCores()
    {
        try
        {
            int physical = 0, logical = 0;
            using var searcher = new ManagementObjectSearcher("SELECT NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            foreach (var cpu in searcher.Get())
            {
                physical += Convert.ToInt32(cpu["NumberOfCores"], System.Globalization.CultureInfo.InvariantCulture);
                logical += Convert.ToInt32(cpu["NumberOfLogicalProcessors"], System.Globalization.CultureInfo.InvariantCulture);
            }
            return (physical > 0 ? physical : null, logical > 0 ? logical : null);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or InvalidCastException)
        {
            return (null, null);
        }
    }

    private async Task<object> MachineInfoAsync()
    {
        var snapshot = await source.SnapshotAsync();
        var hardware = Profile(snapshot);
        var caps = snapshot.Capabilities;
        var profile = snapshot.Settings.Profile;
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return new
        {
            model = snapshot.DeviceName,
            biosVersion = snapshot.BiosVersion,
            cpu = snapshot.CpuName,
            gpu = snapshot.GpuName,
            logicalProcessors = Environment.ProcessorCount,
            memoryGb = Math.Round(memory / 1024.0 / 1024 / 1024, 1),
            operatingSystem = Environment.OSVersion.VersionString,
            openSenseVersion = snapshot.EngineVersion,
            context = hardware.Describe(),
            hardwareProfile = hardware,
            operatingModes = caps.OperatingModes.Select(m => m.ToString()),
            fans = caps.Fans.Select(f => f.Name),
            thermalLimitsCelsius = snapshot.Latest?.Limits,
            cpuTemperatureSource = snapshot.TemperatureSources.Cpu.Sensor?.ToString(),
            gpuTemperatureSource = snapshot.TemperatureSources.Gpu.Sensor?.ToString(),
            settings = new
            {
                fanControl = profile.Mode,
                chosenOperatingMode = profile.OperatingMode,
                autoBoost = profile.AutoBoost,
            },
        };
    }

    private static object Status(EngineSnapshot snapshot)
    {
        if (snapshot.Latest is not { } t)
            throw new ToolException("OpenSense has no reading yet; try again in a few seconds.");
        return Row(t, includeTime: false);
    }

    private static object History(IReadOnlyList<Telemetry> samples, int points)
    {
        if (samples.Count == 0)
            throw new ToolException("OpenSense has no readings yet; try again in a few seconds.");
        var start = samples[0].Timestamp;
        var step = Math.Max(1.0, (samples.Count - 1) / (double)Math.Max(1, points - 1));
        var rows = new List<object>();
        for (double i = 0; Math.Round(i) < samples.Count && rows.Count < points; i += step)
        {
            var sample = samples[(int)Math.Round(i)];
            rows.Add(Row(sample, includeTime: true, start));
        }
        if (Math.Round(rows.Count * step) < samples.Count - 1 && rows.Count < points)
            rows.Add(Row(samples[^1], includeTime: true, start));
        return new { samples = samples.Count, seconds = (samples[^1].Timestamp - start).TotalSeconds, rows };
    }

    private static object Row(Telemetry t, bool includeTime, DateTime? start = null) => new
    {
        tSeconds = includeTime && start is { } s ? Math.Round((t.Timestamp - s).TotalSeconds) : (double?)null,
        cpu = new
        {
            temperatureC = R(t.CpuTemperature),
            loadPercent = R(t.CpuLoad),
            powerW = R(t.CpuPower),
            clockMhz = R(t.CpuClockMhz),
            powerLimit1W = R(t.CpuPowerLimit1),
            powerLimit2W = R(t.CpuPowerLimit2),
            powerControl = t.CpuPowerControl is { } c
                ? new { preset = c.Preset, state = c.State, targetPl1W = R(c.Target1W), targetPl2W = R(c.Target2W), appliedPl1W = R(c.Applied1W), appliedPl2W = R(c.Applied2W), ceilingC = c.CeilingC, overwrites = c.Overwrites }
                : null,
            vidV = t.CpuTuning?.VidV is { } vid ? Math.Round(vid, 3) : (double?)null,
            tuning = includeTime ? null : t.CpuTuning,
            throttle = t.CpuThrottle == CpuThrottleReason.None ? null : t.CpuThrottle.ToString(),
        },
        gpu = new
        {
            asleep = t.GpuAsleep ? true : (bool?)null,
            temperatureC = R(t.GpuTemperature),
            loadPercent = R(t.GpuLoad),
            powerW = R(t.GpuPower),
            powerLimitW = R(t.GpuPowerLimit),
            clockMhz = t.GpuClockMhz,
            memoryClockMhz = t.GpuMemoryClockMhz,
            throttle = t.GpuThrottleReasons is { } r and not 0 ? GpuThrottle.Describe(r) : null,
        },
        totalCpuGpuPowerW = R(t.TotalPower),
        fans = t.Fans.Select(f => new { fan = f.Id.ToString(), rpm = f.Rpm, behavior = f.Behavior.ToString(), boostPercent = f.BoostPercent }),
        operatingMode = t.OperatingMode?.ToString(),
        onAcPower = t.OnAcPower,
        fanControl = t.EffectiveMode.ToString(),
        fanLock = t.FanLock?.ToString(),
    };

    private static double? R(double? value) => value is { } v ? Math.Round(v, 1) : null;

    private static int Seconds(JsonObject? arguments, int fallback) => Clamp(arguments, "seconds", fallback, 5, 3600);

    private static int Clamp(JsonObject? arguments, string name, int fallback, int min, int max) =>
        arguments?[name] is JsonValue value && value.TryGetValue<int>(out var number) ? Math.Clamp(number, min, max) : fallback;

    private static JsonObject Tool(string name, string description, JsonObject properties) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["destructiveHint"] = false, ["openWorldHint"] = false },
    };

    /// <summary>A tool that changes a setting: not read-only, not destructive (it can be put back), and the same call twice does the same.</summary>
    private static JsonObject ChangingTool(string name, string description, JsonObject properties, string? required = null)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required is not null)
            schema["required"] = new JsonArray(required);
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
            ["annotations"] = new JsonObject { ["readOnlyHint"] = false, ["destructiveHint"] = false, ["idempotentHint"] = true, ["openWorldHint"] = false },
        };
    }

    private static JsonObject Integer(string description, int fallback) => new()
    {
        ["type"] = "integer",
        ["description"] = description,
        ["default"] = fallback,
    };
}

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenSense.Core.Analysis;
using OpenSense.Core.Control;
using OpenSense.Core.Ipc;
using OpenSense.Core.Monitoring;

namespace OpenSense.Mcp;

/// <summary>
/// The read-only tools an AI agent gets: what the machine is, how it is doing now and over the last minutes, and a
/// ready-made reading of whether it is thermally or power limited. Nothing here changes a setting.
/// </summary>
public sealed class AgentTools(IAgentSource source)
{
    public const string Instructions =
        "OpenSense reads a gaming laptop's sensors. Use machine_info once for what the hardware is, analyze_thermals for " +
        "'is it throttling / hitting the thermal wall / power limited', current_status for a live reading, and recent_history " +
        "for a time series. To diagnose a workload: have the user run it (for example in Turbo mode), then call analyze_thermals " +
        "with seconds covering the run. Terms: PL1 is the CPU's sustained power limit and PL2 its short boost limit, both set " +
        "by the operating mode; 'power-limited' means clocks are held by a power limit, 'thermal-limited' means by temperature; " +
        "a GPU 'power cap' under full load is normal on laptops. These tools are read-only and cannot change any setting.";

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

    public async Task<JsonNode> CallAsync(string name, JsonObject? arguments)
    {
        object result = name switch
        {
            "machine_info" => await MachineInfoAsync(),
            "current_status" => Status(await source.SnapshotAsync()),
            "recent_history" => History(await source.HistoryAsync(Seconds(arguments, 300)), Clamp(arguments, "points", 60, 2, 240)),
            "analyze_thermals" => ThermalAnalyzer.Analyze(await source.HistoryAsync(Seconds(arguments, 300))),
            _ => throw new ToolException($"Unknown tool {name}."),
        };
        return JsonSerializer.SerializeToNode(result, Json)!;
    }

    private async Task<object> MachineInfoAsync()
    {
        var snapshot = await source.SnapshotAsync();
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

    private static JsonObject Integer(string description, int fallback) => new()
    {
        ["type"] = "integer",
        ["description"] = description,
        ["default"] = fallback,
    };
}

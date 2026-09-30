using System.Text.Json.Nodes;
using OpenSense.Core.Control;
using OpenSense.Core.Hardware;
using OpenSense.Core.Ipc;
using OpenSense.Mcp;

namespace OpenSense.Core.Tests;

public sealed class McpServerTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeSource(EngineSnapshot? snapshot, IReadOnlyList<Telemetry>? history, bool running = true) : IAgentSource
    {
        public Task<EngineSnapshot> SnapshotAsync() =>
            running && snapshot is not null ? Task.FromResult(snapshot) : throw new ToolException("The OpenSense service isn't running.");

        public Task<IReadOnlyList<Telemetry>> HistoryAsync(int seconds) =>
            running ? Task.FromResult(history ?? []) : throw new ToolException("The OpenSense service isn't running.");
    }

    private static IReadOnlyList<Telemetry> Samples(int count) =>
    [
        .. Enumerable.Range(0, count).Select(i => new Telemetry
        {
            Timestamp = Start.AddSeconds(i),
            CpuTemperature = 70,
            CpuLoad = 90,
            CpuPower = 60,
            CpuPowerLimit1 = 55,
            CpuThrottle = OpenSense.Core.Monitoring.CpuThrottleReason.PowerLimit,
            GpuTemperature = 70,
            GpuLoad = 90,
            GpuPower = 90,
            GpuThrottleReasons = OpenSense.Core.Monitoring.GpuThrottle.SoftwarePowerCap,
            OperatingMode = OperatingMode.Turbo,
        }),
    ];

    private static McpServer Server(bool running = true, int samples = 30)
    {
        var history = Samples(samples);
        var snapshot = new EngineSnapshot { DeviceName = "Predator Test 1", CpuName = "Test CPU", GpuName = "Test GPU", Latest = history.Count > 0 ? history[^1] : null };
        return new McpServer(new AgentTools(new FakeSource(snapshot, history, running)), "1.2.3");
    }

    private static async Task<JsonObject> Ask(McpServer server, string request) =>
        JsonNode.Parse(await server.HandleLineAsync(request) ?? throw new InvalidOperationException("no response"))!.AsObject();

    private static JsonObject ToolCall(string name, string arguments = "{}") =>
        JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"" + name + "\",\"arguments\":" + arguments + "}}")!.AsObject();

    private static async Task<(bool IsError, string Text)> Call(McpServer server, string name, string arguments = "{}")
    {
        var response = await Ask(server, ToolCall(name, arguments).ToJsonString());
        var result = response["result"]!.AsObject();
        return (result["isError"]!.GetValue<bool>(), result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Initialize_answers_with_the_clients_protocol_version_and_the_tools_capability()
    {
        var response = await Ask(Server(), """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""");

        Assert.Equal(1, response["id"]!.GetValue<int>());
        Assert.Equal("2025-03-26", response["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(response["result"]!["capabilities"]!["tools"]);
        Assert.Equal("opensense", response["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Contains("read-only", response["result"]!["instructions"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unknown_protocol_version_gets_the_latest_supported_one()
    {
        var response = await Ask(Server(), """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}""");
        Assert.Equal("2025-06-18", response["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task Notifications_and_blank_lines_get_no_response()
    {
        var server = Server();
        Assert.Null(await server.HandleLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        Assert.Null(await server.HandleLineAsync("   "));
    }

    [Fact]
    public async Task Every_tool_is_declared_read_only()
    {
        var response = await Ask(Server(), """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var tools = response["result"]!["tools"]!.AsArray();

        Assert.Equal(["analyze_thermals", "current_status", "machine_info", "recent_history"], tools.Select(t => t!["name"]!.GetValue<string>()).Order().ToArray());
        Assert.All(tools, t => Assert.True(t!["annotations"]!["readOnlyHint"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task Analyze_thermals_returns_a_verdict_with_findings()
    {
        var (isError, text) = await Call(Server(), "analyze_thermals", """{"seconds":60}""");

        Assert.False(isError);
        var report = JsonNode.Parse(text)!;
        Assert.Equal("power-limited", report["verdict"]!.GetValue<string>());
        Assert.Contains(report["findings"]!.AsArray(), f => f!["area"]!.GetValue<string>() == "cpu");
    }

    [Fact]
    public async Task Current_status_includes_power_limits_and_throttle_reasons()
    {
        var (isError, text) = await Call(Server(), "current_status");

        Assert.False(isError);
        var status = JsonNode.Parse(text)!;
        Assert.Equal(55, status["cpu"]!["powerLimit1W"]!.GetValue<double>());
        Assert.Equal("PowerLimit", status["cpu"]!["throttle"]!.GetValue<string>());
        Assert.Equal("power cap (software)", status["gpu"]!["throttle"]![0]!.GetValue<string>());
        Assert.Equal(150, status["totalCpuGpuPowerW"]!.GetValue<double>());
    }

    [Fact]
    public async Task Recent_history_is_downsampled_to_the_requested_points()
    {
        var (_, text) = await Call(Server(samples: 100), "recent_history", """{"seconds":100,"points":10}""");

        var history = JsonNode.Parse(text)!;
        Assert.Equal(100, history["samples"]!.GetValue<int>());
        Assert.InRange(history["rows"]!.AsArray().Count, 9, 10);
        Assert.Equal(0, history["rows"]![0]!["tSeconds"]!.GetValue<double>());
    }

    [Fact]
    public async Task Machine_info_names_the_hardware()
    {
        var (_, text) = await Call(Server(), "machine_info");

        var info = JsonNode.Parse(text)!;
        Assert.Equal("Predator Test 1", info["model"]!.GetValue<string>());
        Assert.Equal("Test CPU", info["cpu"]!.GetValue<string>());
        Assert.Equal("Test GPU", info["gpu"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_stopped_service_is_reported_as_a_tool_error_not_a_protocol_error()
    {
        var response = await Ask(Server(running: false), ToolCall("current_status").ToJsonString());

        Assert.Null(response["error"]);
        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("isn't running", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_tools_and_methods_are_protocol_errors()
    {
        var server = Server();
        Assert.Equal(-32602, (await Ask(server, ToolCall("set_fan_speed").ToJsonString()))["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32601, (await Ask(server, """{"jsonrpc":"2.0","id":3,"method":"resources/list"}"""))["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32700, (await Ask(server, "{ not json"))["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Nothing_offered_can_change_a_setting()
    {
        var response = await Ask(Server(), """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var names = response["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>());
        Assert.DoesNotContain(names, n => n.StartsWith("set", StringComparison.OrdinalIgnoreCase) || n.Contains("write", StringComparison.OrdinalIgnoreCase));
    }
}

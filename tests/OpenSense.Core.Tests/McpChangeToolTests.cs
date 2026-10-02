using System.Text.Json.Nodes;
using OpenSense.Core.Control;
using OpenSense.Core.Hardware;
using OpenSense.Core.Ipc;
using OpenSense.Core.Settings;
using OpenSense.Mcp;

namespace OpenSense.Core.Tests;

public sealed class McpChangeToolTests
{
    private sealed class RecordingSource(ControlProfile start) : IAgentSource
    {
        public ControlProfile Profile { get; private set; } = start;
        public int Sets;

        public Task<EngineSnapshot> SnapshotAsync() =>
            Task.FromResult(new EngineSnapshot { DeviceName = "Predator Test 1", Settings = new MachineSettings { Profile = Profile } });

        public Task<IReadOnlyList<Telemetry>> HistoryAsync(int seconds) => Task.FromResult<IReadOnlyList<Telemetry>>([]);

        public Task SetProfileAsync(ControlProfile profile)
        {
            Profile = profile;
            Sets++;
            return Task.CompletedTask;
        }
    }

    private static McpServer Server(RecordingSource source, bool allowChanges) => new(new AgentTools(source, allowChanges), "1.2.3");

    private static async Task<JsonObject> Ask(McpServer server, string request) =>
        JsonNode.Parse(await server.HandleLineAsync(request) ?? throw new InvalidOperationException("no response"))!.AsObject();

    private static async Task<(bool IsError, string Text)> Call(McpServer server, string name, string arguments = "{}")
    {
        var response = await Ask(server, "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"" + name + "\",\"arguments\":" + arguments + "}}");
        if (response["error"] is { } error)
            return (true, error["message"]!.GetValue<string>());
        var result = response["result"]!.AsObject();
        return (result["isError"]!.GetValue<bool>(), result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task By_default_the_server_offers_no_changing_tool_and_changes_nothing()
    {
        var source = new RecordingSource(new ControlProfile());
        var server = Server(source, allowChanges: false);
        var tools = (await Ask(server, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}"))["result"]!["tools"]!.AsArray();
        Assert.Equal(4, tools.Count);
        Assert.All(tools, t => Assert.True(t!["annotations"]!["readOnlyHint"]!.GetValue<bool>()));

        var (isError, text) = await Call(server, "set_performance_profile", "{\"profile\":\"unleash\"}");
        Assert.True(isError);
        Assert.Contains("Unknown tool", text);
        Assert.Equal(0, source.Sets);
    }

    [Fact]
    public async Task With_changes_allowed_the_changing_tools_are_listed_and_marked_as_such()
    {
        var server = Server(new RecordingSource(new ControlProfile()), allowChanges: true);
        var response = await Ask(server, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");
        var tools = response["result"]!["tools"]!.AsArray();
        Assert.Equal(7, tools.Count);
        var changing = tools.Where(t => t!["name"]!.GetValue<string>() is "set_performance_profile" or "set_cpu_power" or "restore_firmware_power").ToList();
        Assert.Equal(3, changing.Count);
        Assert.All(changing, t => Assert.False(t!["annotations"]!["readOnlyHint"]!.GetValue<bool>()));

        var init = await Ask(server, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}");
        Assert.Contains("set_cpu_power", init["result"]!["instructions"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_performance_profile_is_applied_on_top_of_the_current_settings()
    {
        var mine = new ControlProfile { CoolBoost = true, ModeKey = ModeKeyAction.TurboToggle };
        var source = new RecordingSource(mine);
        var (isError, text) = await Call(Server(source, true), "set_performance_profile", "{\"profile\":\"quiet_performance\"}");
        Assert.False(isError);
        Assert.Equal(ProfilePresets.Apply(mine, PerformanceProfile.QuietPerformance), source.Profile);
        Assert.Equal(1, source.Sets);
        Assert.Contains("quiet", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unknown_profile_is_refused()
    {
        var source = new RecordingSource(new ControlProfile());
        var (isError, _) = await Call(Server(source, true), "set_performance_profile", "{\"profile\":\"warp\"}");
        Assert.True(isError);
        Assert.Equal(0, source.Sets);
    }

    [Fact]
    public async Task Custom_cpu_power_is_clamped_and_reported()
    {
        var source = new RecordingSource(new ControlProfile());
        var (isError, text) = await Call(Server(source, true), "set_cpu_power", "{\"pl1_w\":500,\"pl2_w\":900,\"window_s\":1000,\"ceiling_c\":120}");
        Assert.False(isError);
        var target = JsonNode.Parse(text)!["now"]!["cpuPower"]!["target"]!;
        Assert.Equal(CpuPowerPresets.MaxPl1W, target["pl1W"]!.GetValue<double>());
        Assert.Equal(CpuPowerPresets.MaxPl2W, target["pl2W"]!.GetValue<double>());
        Assert.Equal(CpuPowerPresets.MaxWindowS, target["windowSeconds"]!.GetValue<double>());
        Assert.Equal(CpuPowerPresets.MaxCeilingC, target["ceilingC"]!.GetValue<int>());
        Assert.Equal(CpuPowerPreset.Custom, source.Profile.CpuPower.Preset);
    }

    [Fact]
    public async Task A_cpu_power_preset_can_carry_a_temperature_target()
    {
        var source = new RecordingSource(new ControlProfile());
        var (isError, _) = await Call(Server(source, true), "set_cpu_power", "{\"preset\":\"quiet\",\"ceiling_c\":80}");
        Assert.False(isError);
        Assert.Equal(CpuPowerPreset.Quiet, source.Profile.CpuPower.Preset);
        Assert.Equal(80, source.Profile.CpuPower.Target()!.CeilingC);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"preset\":\"warp\"}")]
    [InlineData("{\"preset\":\"eco\",\"pl1_w\":40}")]
    public async Task Cpu_power_needs_a_preset_or_custom_values_and_not_both(string arguments)
    {
        var source = new RecordingSource(new ControlProfile());
        var (isError, _) = await Call(Server(source, true), "set_cpu_power", arguments);
        Assert.True(isError);
        Assert.Equal(0, source.Sets);
    }

    [Fact]
    public async Task Restoring_gives_the_limits_back_to_the_firmware()
    {
        var source = new RecordingSource(new ControlProfile { CpuPower = new CpuPowerSettings { Preset = CpuPowerPreset.Eco } });
        var (isError, _) = await Call(Server(source, true), "restore_firmware_power");
        Assert.False(isError);
        Assert.Equal(CpuPowerPreset.Firmware, source.Profile.CpuPower.Preset);
    }
}

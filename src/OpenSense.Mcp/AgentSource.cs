using OpenSense.Core.Control;
using OpenSense.Core.Ipc;

namespace OpenSense.Mcp;

/// <summary>A tool could not run; the message is what the agent is told.</summary>
public sealed class ToolException(string message) : Exception(message);

/// <summary>Where the tools get their data from: the running OpenSense service.</summary>
public interface IAgentSource
{
    /// <exception cref="ToolException">The service isn't running.</exception>
    Task<EngineSnapshot> SnapshotAsync();

    /// <summary>The last <paramref name="seconds"/> of telemetry, oldest first.</summary>
    Task<IReadOnlyList<Telemetry>> HistoryAsync(int seconds);

    /// <summary>Sets the machine's control profile. Only reached when the server was started with <c>--allow-changes</c>.</summary>
    Task SetProfileAsync(ControlProfile profile) => throw new ToolException("This source can't change settings.");
}

/// <summary>Reads through the service's pipe, and sets the control profile when the server was allowed to change things.</summary>
public sealed class PipeAgentSource : IAgentSource
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<EngineSnapshot> SnapshotAsync()
    {
        using var connection = await ConnectAsync();
        return await connection.Service.GetSnapshotAsync();
    }

    public async Task<IReadOnlyList<Telemetry>> HistoryAsync(int seconds)
    {
        using var connection = await ConnectAsync();
        try
        {
            return await connection.Service.GetHistoryAsync(seconds);
        }
        catch (StreamJsonRpc.RemoteMethodNotFoundException)
        {
            throw new ToolException("The installed OpenSense service is too old to keep a history. Update OpenSense.");
        }
    }

    public async Task SetProfileAsync(ControlProfile profile)
    {
        using var connection = await ConnectAsync();
        await connection.Service.SetProfileAsync(profile);
    }

    private static async Task<OpenSenseConnection> ConnectAsync()
    {
        try
        {
            return await OpenSensePipe.ConnectAsync(Timeout)
                ?? throw new ToolException("The OpenSense service isn't running (nothing answered on its pipe). Start the OpenSense service.");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ToolException(ex.Message);
        }
    }
}

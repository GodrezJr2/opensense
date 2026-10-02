using System.Text.Json.Nodes;

namespace OpenSense.Mcp;

/// <summary>
/// A minimal Model Context Protocol server over JSON-RPC 2.0: initialize, ping, tools/list and tools/call, which is all
/// tools need. One message per line, as the stdio transport defines.
/// </summary>
public sealed class McpServer(AgentTools tools, string serverVersion)
{
    private const string LatestProtocol = "2025-06-18";
    private static readonly string[] SupportedProtocols = ["2025-06-18", "2025-03-26", "2024-11-05"];

    /// <summary>The response to <paramref name="line"/>, or null when none is due (a notification, or an empty line).</summary>
    public async Task<string?> HandleLineAsync(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        JsonNode? request;
        try
        {
            request = JsonNode.Parse(line);
        }
        catch (System.Text.Json.JsonException)
        {
            return Error(null, -32700, "Parse error").ToJsonString();
        }
        if (request is not JsonObject message || message["method"]?.GetValue<string>() is not { } method)
            return request is JsonObject { } && request["id"] is not null ? Error(request["id"], -32600, "Invalid request").ToJsonString() : null;

        var id = message["id"]?.DeepClone();
        if (id is null)
            return null; // a notification, e.g. notifications/initialized

        try
        {
            var result = await DispatchAsync(method, message["params"] as JsonObject);
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();
        }
        catch (MethodNotFound)
        {
            return Error(id, -32601, $"Method not found: {method}").ToJsonString();
        }
        catch (InvalidParams ex)
        {
            return Error(id, -32602, ex.Message).ToJsonString();
        }
    }

    private async Task<JsonNode> DispatchAsync(string method, JsonObject? parameters)
    {
        switch (method)
        {
            case "initialize":
                var requested = parameters?["protocolVersion"]?.GetValue<string>();
                return new JsonObject
                {
                    ["protocolVersion"] = requested is not null && SupportedProtocols.Contains(requested) ? requested : LatestProtocol,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "opensense", ["version"] = serverVersion },
                    ["instructions"] = tools.InstructionsText,
                };
            case "ping":
                return new JsonObject();
            case "tools/list":
                return new JsonObject { ["tools"] = tools.AllDefinitions() };
            case "tools/call":
                var name = parameters?["name"]?.GetValue<string>() ?? throw new InvalidParams("Missing tool name.");
                if (!tools.AllDefinitions().Any(t => t?["name"]?.GetValue<string>() == name))
                    throw new InvalidParams($"Unknown tool: {name}");
                return await CallToolAsync(name, parameters?["arguments"] as JsonObject);
            default:
                throw new MethodNotFound();
        }
    }

    /// <summary>A tool that can't run answers with isError so the agent sees why and can tell the user.</summary>
    private async Task<JsonNode> CallToolAsync(string name, JsonObject? arguments)
    {
        try
        {
            var data = await tools.CallAsync(name, arguments);
            return Content(data.ToJsonString(), isError: false);
        }
        catch (ToolException ex)
        {
            return Content(ex.Message, isError: true);
        }
        catch (Exception ex) when (ex is IOException or StreamJsonRpc.RemoteRpcException or StreamJsonRpc.ConnectionLostException or TimeoutException)
        {
            return Content("Lost the connection to the OpenSense service: " + ex.Message, isError: true);
        }
    }

    private static JsonObject Content(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private sealed class MethodNotFound : Exception;

    private sealed class InvalidParams(string message) : Exception(message);
}

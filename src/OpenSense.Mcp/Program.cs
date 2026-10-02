using System.Text;
using OpenSense.Mcp;

// OpenSense for AI agents: a Model Context Protocol server on stdio. By default it only reads: it asks the OpenSense
// service for readings and never sends it a setting. Started with --allow-changes (or OPENSENSE_MCP_ALLOW_CHANGES=1)
// it also offers tools that set a performance profile or the CPU's power limits. stdout carries the protocol only;
// anything else goes to stderr.

var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0";
var allowChanges = args.Contains("--allow-changes") || Environment.GetEnvironmentVariable("OPENSENSE_MCP_ALLOW_CHANGES") == "1";
var server = new McpServer(new AgentTools(new PipeAgentSource(), allowChanges), version);

var utf8 = new UTF8Encoding(false);
using var input = new StreamReader(Console.OpenStandardInput(), utf8);
using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };

while (await input.ReadLineAsync() is { } line)
{
    if (await server.HandleLineAsync(line) is { } response)
        await output.WriteLineAsync(response);
}

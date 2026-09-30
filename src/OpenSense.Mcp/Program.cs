using System.Text;
using OpenSense.Mcp;

// OpenSense for AI agents: a Model Context Protocol server on stdio. Read-only: it asks the OpenSense service for
// readings and never sends it a setting. stdout carries the protocol only; anything else goes to stderr.

var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0";
var server = new McpServer(new AgentTools(new PipeAgentSource()), version);

var utf8 = new UTF8Encoding(false);
using var input = new StreamReader(Console.OpenStandardInput(), utf8);
using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };

while (await input.ReadLineAsync() is { } line)
{
    if (await server.HandleLineAsync(line) is { } response)
        await output.WriteLineAsync(response);
}

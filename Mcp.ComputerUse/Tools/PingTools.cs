using ModelContextProtocol.Server;
using System.ComponentModel;
using Mcp.ComputerUse.Json;
using Microsoft.Extensions.Logging;

namespace Mcp.ComputerUse.Tools;

[McpServerToolType]
public sealed class PingTools
{
    private readonly ILogger<PingTools> _log;
    public PingTools(ILogger<PingTools> log) => _log = log;

    [McpServerTool, Description("Returns a hello message and server timestamp. Confirms wire connectivity.")]
    public PingResult Ping([Description("Optional message to echo back. Empty string = default 'pong'.")] string message = "")
    {
        _log.LogDebug("tool_call tool={Tool}", nameof(Ping));
        return new(string.IsNullOrEmpty(message) ? "pong" : message, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
}

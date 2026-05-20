using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Mcp.ComputerUse.Core;
using Mcp.ComputerUse.Json;
using Mcp.ComputerUse.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace Mcp.ComputerUse;

/// <summary>
/// Shared DI/host configuration for both the entrypoint (<see cref="Program"/>)
/// and tests. Keeping it out of top-level statements lets <c>HostStartupTests</c>
/// exercise the same registrations the published binary uses.
/// </summary>
internal static class HostStartup
{
    /// <summary>
    /// JSON options that merge our source-gen context (<see cref="McpJsonContext"/>)
    /// with the SDK's default resolver. Required so AOT publish can locate
    /// <c>JsonTypeInfo</c> for return types like <c>PingResult</c> when the SDK
    /// generates parameter/return schemas at tool-registration time.
    /// </summary>
    public static JsonSerializerOptions BuildJsonOptions()
    {
        var sdkDefault = McpJsonUtilities.DefaultOptions;
        var combined = new JsonSerializerOptions(sdkDefault)
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(
                McpJsonContext.Default,
                sdkDefault.TypeInfoResolver!)
        };
        return combined;
    }

    public static void Configure(IHostApplicationBuilder builder, AppOptions opts)
    {
        // stdio MCP: stdout is reserved for the protocol. Logs must go to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(opts.LogLevel);

        builder.Services
            .AddSingleton(opts)
            .AddSingleton<MonitorRegistry>()
            .AddSingleton<CoordinateMapper>()
            .AddSingleton<ScalePlanCache>()
            .AddSingleton<ScreenshotStorage>()
            .AddSingleton<ScreenCaptureService>()
            .AddSingleton<VisualFlash>()
            .AddSingleton<InputService>()
            .AddSingleton<FileService>();

        var jsonOpts = BuildJsonOptions();

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<PingTools>(jsonOpts)
            .WithTools<MonitorTools>(jsonOpts)
            .WithTools<ScreenTools>(jsonOpts)
            .WithTools<MouseTools>(jsonOpts)
            .WithTools<KeyboardTools>(jsonOpts)
            .WithTools<FileTools>(jsonOpts);
    }
}

using FluentAssertions;
using Mcp.ComputerUse.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using Xunit;

namespace Mcp.ComputerUse.Tests;

public class HostStartupTests
{
    [Fact]
    public void BuildJsonOptions_combines_McpJsonContext_with_SDK_default_resolver()
    {
        // The AOT-published exe crashed with:
        //   "JsonTypeInfo metadata for type 'Mcp.ComputerUse.Json.PingResult'
        //    was not provided by TypeInfoResolver of type
        //    '[ModelContextProtocol.McpJsonUtilities+JsonContext,
        //      Microsoft.Extensions.AI.AIJsonUtilities+JsonContext]'."
        //
        // The fix is HostStartup.BuildJsonOptions() — it prepends our
        // McpJsonContext source-gen resolver to the SDK's default chain so the
        // schema exporter can find metadata for our DTOs even without
        // reflection fallback (which AOT strips).
        var opts = HostStartup.BuildJsonOptions();

        // First resolver in the chain must be our source-gen context so AOT
        // can locate JsonTypeInfo without falling back to reflection.
        opts.TypeInfoResolverChain.Should().NotBeEmpty();
        opts.TypeInfoResolverChain.First()
            .Should().BeOfType<McpJsonContext>("McpJsonContext must be first so it answers PingResult etc. before the SDK resolvers");

        // And our resolver does provide PingResult (and friends) directly —
        // this is what the SDK's JsonSchemaExporter ultimately needs.
        ((System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver)McpJsonContext.Default).GetTypeInfo(typeof(PingResult), opts).Should().NotBeNull();
        ((System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver)McpJsonContext.Default).GetTypeInfo(typeof(ListMonitorsResult), opts).Should().NotBeNull();
        ((System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver)McpJsonContext.Default).GetTypeInfo(typeof(ScreenshotMeta), opts).Should().NotBeNull();
    }

    [Fact]
    public void Host_builds_and_registers_McpServerTools_without_errors()
    {
        // Boot the host exactly the way Program.cs does. The MCP SDK creates
        // McpServerTool descriptors via factory delegates; resolving the
        // IEnumerable<McpServerTool> invokes every delegate and surfaces any
        // construction failure (e.g. malformed tool method signatures).
        var builder = Host.CreateApplicationBuilder([]);
        HostStartup.Configure(builder, AppOptions.Parse([]));

        using var host = builder.Build();

        var act = () => host.Services.GetServices<McpServerTool>().ToList();
        var tools = act.Should().NotThrow().Subject;
        tools.Should().NotBeEmpty("PingTools and friends should be registered");
    }
}

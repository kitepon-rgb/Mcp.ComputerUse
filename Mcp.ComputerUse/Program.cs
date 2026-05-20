using Mcp.ComputerUse.Native;
using Microsoft.Extensions.Hosting;

// Defensive: ensure PerMonitorV2 even before manifest is honored.
Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

var opts = Mcp.ComputerUse.AppOptions.Parse(args);

var builder = Host.CreateApplicationBuilder(args);
Mcp.ComputerUse.HostStartup.Configure(builder, opts);

await builder.Build().RunAsync();

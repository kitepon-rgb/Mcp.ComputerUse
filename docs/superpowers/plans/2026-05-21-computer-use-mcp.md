# Computer-Use MCP Server Implementation Plan

> **For Claude:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows computer-use MCP server in C# / .NET 10 Native AOT exposing screenshot, mouse, keyboard, file ops, and shell tools — consumable from Claude Code over stdio, with screenshots delivered as native `image` content blocks.

**Architecture:** stdio MCP host (Microsoft.Extensions.Hosting) → DI-resolved service layer (MonitorRegistry, CoordinateMapper, ScreenCaptureService, InputService, FileService) → tool classes registered explicitly (`.WithTools<T>()`) for AOT cleanliness → Win32 P/Invoke via `[LibraryImport]` source generators → ImageSharp 4.0 for PNG encoding. Coordinates flow native-pixels → scaled-pixels in screenshot tool (cached `ScalePlan` per monitor), reversed in every mouse tool via `coord_space="model"`.

**Tech Stack:** .NET 10 (net10.0-windows, win-x64), Native AOT, ModelContextProtocol 1.3.0, Microsoft.Extensions.Hosting 9.*, SixLabors.ImageSharp 4.0.0, xUnit + FluentAssertions for tests.

**Spec:** `docs/superpowers/specs/2026-05-21-computer-use-mcp-design.md`

---

## Conventions

- **TDD:** Each component starts with a failing unit test (where unit-testable). Win32-touching components get a smoke integration test that runs on a real Windows display + manual E2E verification from Claude Code.
- **Files:** snake_case for JSON wire format, PascalCase for C# types. One responsibility per file.
- **Commits:** After every passing test or coherent edit. Conventional commits (`feat:`, `test:`, `chore:`, `fix:`, `refactor:`).
- **AOT discipline:** any new DTO must be added to `McpJsonContext` in the same commit it's introduced.
- **Logging:** never `Console.WriteLine` — always `ILogger<T>` (goes to stderr).
- **Run:** `dotnet build -c Release` for fast iteration; `dotnet publish -c Release -r win-x64` for AOT verification at end of each stage.

---

## Chunk 0: Project Bootstrap

Goal: turn the Hello-World skeleton into a runnable stdio MCP server that exposes one trivial `ping` tool to confirm the wire is up. End-state: Claude Code can connect, list tools, call `ping`, get a response.

### Task 0.1: Initialize git

**Files:**
- Create: `.gitignore`
- Create: `.gitattributes`

- [ ] **Step 1: git init**

```powershell
git init
git branch -M main
```

- [ ] **Step 2: Write `.gitignore`**

```gitignore
bin/
obj/
*.user
*.suo
.vs/
*.binlog
publish/
TestResults/
artifacts/
```

- [ ] **Step 3: Write `.gitattributes`**

```gitattributes
* text=auto eol=crlf
*.sh text eol=lf
*.png binary
*.pdf binary
```

- [ ] **Step 4: Initial commit**

```powershell
git add .gitignore .gitattributes docs/
git commit -m "chore: initial commit with research docs and design spec"
```

### Task 0.2: Migrate csproj to AOT-ready Windows target

**Files:**
- Modify: `Mcp.ComputerUse/Mcp.ComputerUse.csproj`
- Create: `Mcp.ComputerUse/app.manifest`

- [ ] **Step 1: Replace csproj contents**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PublishAot>true</PublishAot>
    <OptimizationPreference>Size</OptimizationPreference>
    <InvariantGlobalization>true</InvariantGlobalization>
    <StripSymbols>true</StripSymbols>
    <IsAotCompatible>true</IsAotCompatible>
    <TrimmerSingleWarn>false</TrimmerSingleWarn>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <RootNamespace>Mcp.ComputerUse</RootNamespace>
    <AssemblyName>mcp-computeruse</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ModelContextProtocol" Version="1.3.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="9.0.0" />
    <PackageReference Include="SixLabors.ImageSharp" Version="4.0.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write `app.manifest`**

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <asmv3:application xmlns:asmv3="urn:schemas-microsoft-com:asm.v3">
    <asmv3:windowsSettings xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">True/PM</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </asmv3:windowsSettings>
  </asmv3:application>
</assembly>
```

- [ ] **Step 3: Restore + verify**

Run: `dotnet restore Mcp.ComputerUse/Mcp.ComputerUse.csproj`
Expected: no errors. Packages resolved.

- [ ] **Step 4: Commit**

```powershell
git add Mcp.ComputerUse/Mcp.ComputerUse.csproj Mcp.ComputerUse/app.manifest
git commit -m "chore: target net10.0-windows, add AOT settings and DPI manifest"
```

### Task 0.3: Scaffold stdio host + ping tool

**Files:**
- Modify: `Mcp.ComputerUse/Program.cs`
- Create: `Mcp.ComputerUse/Tools/PingTools.cs`
- Create: `Mcp.ComputerUse/Json/McpJsonContext.cs`

- [ ] **Step 1: Write `Json/McpJsonContext.cs`**

```csharp
using System.Text.Json.Serialization;

namespace Mcp.ComputerUse.Json;

public sealed record PingResult(string Message, long ServerTimeUnixMs);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PingResult))]
internal partial class McpJsonContext : JsonSerializerContext;
```

- [ ] **Step 2: Write `Tools/PingTools.cs`**

```csharp
using ModelContextProtocol.Server;
using System.ComponentModel;
using Mcp.ComputerUse.Json;

namespace Mcp.ComputerUse.Tools;

[McpServerToolType]
public sealed class PingTools
{
    [McpServerTool, Description("Returns a hello message and server timestamp. Confirms wire connectivity.")]
    public static PingResult Ping([Description("Optional message to echo back")] string? message = null)
        => new(message ?? "pong", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
```

- [ ] **Step 3: Replace `Program.cs`**

```csharp
using Mcp.ComputerUse.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdio MCP: stdout is reserved for the protocol. Logs must go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<PingTools>();

await builder.Build().RunAsync();
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build Mcp.ComputerUse/Mcp.ComputerUse.csproj -c Release`
Expected: succeeds. Some `IL3050` / `IL2026` warnings from MCP SDK / ImageSharp are acceptable (we tighten in Stage 4).

- [ ] **Step 5: Smoke-test stdio handshake**

Use a tiny helper to send an `initialize` request over stdio:

```powershell
$msg = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"0.0.1"}}}'
$msg + "`n" | & dotnet run --project Mcp.ComputerUse -c Release
```

Expected: server prints a JSON-RPC response to stdout containing `"serverInfo"` and `"capabilities":{"tools":{}}`. (Press Ctrl+C to exit.)

- [ ] **Step 6: Commit**

```powershell
git add Mcp.ComputerUse/Program.cs Mcp.ComputerUse/Tools/PingTools.cs Mcp.ComputerUse/Json/McpJsonContext.cs
git commit -m "feat: stdio MCP host with ping tool"
```

### Task 0.4: Wire into Claude Code config

**Files:**
- Create: `claude-mcp.example.json` (documentation, do not commit secrets)

- [ ] **Step 1: Publish AOT binary**

Run: `dotnet publish Mcp.ComputerUse/Mcp.ComputerUse.csproj -c Release -r win-x64`
Expected: produces `Mcp.ComputerUse/bin/Release/net10.0-windows/win-x64/publish/mcp-computeruse.exe` < 25 MB.

- [ ] **Step 2: Write Claude Code MCP config example**

```json
{
  "mcpServers": {
    "computer-use": {
      "command": "C:\\Works\\Mcp.ComputerUse\\Mcp.ComputerUse\\bin\\Release\\net10.0-windows\\win-x64\\publish\\mcp-computeruse.exe",
      "args": []
    }
  }
}
```

- [ ] **Step 3: Manual E2E verify**

Add the server to Claude Code (`claude mcp add` or via UI). In a Claude Code session ask: "call the ping tool with message hello". Expected: tool result `{ "message": "hello", "server_time_unix_ms": <number> }`.

- [ ] **Step 4: Commit**

```powershell
git add claude-mcp.example.json
git commit -m "docs: example Claude Code MCP config"
```

### Task 0.5: Set up test project

**Files:**
- Create: `Mcp.ComputerUse.Tests/Mcp.ComputerUse.Tests.csproj`
- Create: `Mcp.ComputerUse.Tests/SmokeTests.cs`
- Modify: `Mcp.ComputerUse.slnx`

- [ ] **Step 1: Write test csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="FluentAssertions" Version="6.12.1" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Mcp.ComputerUse\Mcp.ComputerUse.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write `SmokeTests.cs`**

```csharp
using FluentAssertions;
using Mcp.ComputerUse.Tools;
using Xunit;

namespace Mcp.ComputerUse.Tests;

public class SmokeTests
{
    [Fact]
    public void Ping_returns_message_and_timestamp()
    {
        var result = PingTools.Ping("hi");
        result.Message.Should().Be("hi");
        result.ServerTimeUnixMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Ping_defaults_to_pong()
    {
        var result = PingTools.Ping(null);
        result.Message.Should().Be("pong");
    }
}
```

- [ ] **Step 3: Add test project to slnx**

Edit `Mcp.ComputerUse.slnx` to include `<Project Path="Mcp.ComputerUse.Tests/Mcp.ComputerUse.Tests.csproj" />`.

- [ ] **Step 4: Run tests**

Run: `dotnet test Mcp.ComputerUse.Tests/Mcp.ComputerUse.Tests.csproj`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```powershell
git add Mcp.ComputerUse.Tests/ Mcp.ComputerUse.slnx
git commit -m "test: scaffold xunit test project with ping smoke tests"
```

---

## Chunk 1: Native Layer + Monitors + Screenshot

Goal: `list_monitors` and `screenshot` tools work end-to-end from Claude Code. Screenshot returns an `image` content block (base64 PNG) plus a text block with `ScalePlan` metadata.

### Task 1.1: Win32 declarations

**Files:**
- Create: `Mcp.ComputerUse/Native/NativeTypes.cs`
- Create: `Mcp.ComputerUse/Native/Win32.cs`

- [ ] **Step 1: Write `Native/NativeTypes.cs`**

```csharp
using System.Runtime.InteropServices;

namespace Mcp.ComputerUse.Native;

[StructLayout(LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct MONITORINFOEX
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
}

[StructLayout(LayoutKind.Sequential)]
public struct INPUT { public uint type; public InputUnion U; }

[StructLayout(LayoutKind.Explicit)]
public struct InputUnion
{
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
    [FieldOffset(0)] public HARDWAREINPUT hi;
}

[StructLayout(LayoutKind.Sequential)]
public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public nuint dwExtraInfo; }

[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public nuint dwExtraInfo; }

[StructLayout(LayoutKind.Sequential)]
public struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }

[StructLayout(LayoutKind.Sequential)]
public struct POINT { public int X; public int Y; }

[StructLayout(LayoutKind.Sequential)]
public struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
public struct BITMAPINFO
{
    public BITMAPINFOHEADER bmiHeader;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1024)] public byte[] bmiColors;
}
```

- [ ] **Step 2: Write `Native/Win32.cs`**

```csharp
using System.Runtime.InteropServices;

namespace Mcp.ComputerUse.Native;

internal static partial class Win32
{
    // INPUT types
    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;

    // MOUSEINPUT.dwFlags
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_HWHEEL = 0x01000;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    public const int WHEEL_DELTA = 120;

    // KEYBDINPUT.dwFlags
    public const uint KEYEVENTF_EXTENDEDKEY = 0x01;
    public const uint KEYEVENTF_KEYUP = 0x02;
    public const uint KEYEVENTF_UNICODE = 0x04;
    public const uint KEYEVENTF_SCANCODE = 0x08;

    // SystemMetrics
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    // BitBlt
    public const uint SRCCOPY = 0x00CC0020;
    public const uint CAPTUREBLT = 0x40000000;
    public const uint DIB_RGB_COLORS = 0;

    // DPI
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    // Monitor enumeration
    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessDpiAwarenessContext(IntPtr value);

    // GDI screen capture
    [LibraryImport("user32.dll")] public static partial IntPtr GetDC(IntPtr hWnd);
    [LibraryImport("user32.dll")] public static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [LibraryImport("gdi32.dll")] public static partial IntPtr CreateCompatibleDC(IntPtr hDC);
    [LibraryImport("gdi32.dll")] public static partial IntPtr CreateCompatibleBitmap(IntPtr hDC, int w, int h);
    [LibraryImport("gdi32.dll")] public static partial IntPtr SelectObject(IntPtr hDC, IntPtr hObj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(IntPtr hdcDest, int x, int y, int cx, int cy, IntPtr hdcSrc, int sx, int sy, uint rop);

    [LibraryImport("gdi32.dll")]
    public static partial int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, IntPtr lpvBits, ref BITMAPINFO bmi, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr hObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(IntPtr hdc);

    // Input
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int X, int Y);

    [LibraryImport("user32.dll")]
    public static partial ushort VkKeyScanW(char ch);
}
```

- [ ] **Step 3: Build**

Run: `dotnet build -c Release`
Expected: succeeds with no new errors.

- [ ] **Step 4: Commit**

```powershell
git add Mcp.ComputerUse/Native/
git commit -m "feat(native): LibraryImport declarations for monitors, input, gdi capture"
```

### Task 1.2: MonitorRegistry

**Files:**
- Create: `Mcp.ComputerUse/Core/MonitorInfo.cs`
- Create: `Mcp.ComputerUse/Core/MonitorRegistry.cs`
- Create: `Mcp.ComputerUse.Tests/MonitorRegistryTests.cs`

- [ ] **Step 1: Write `MonitorInfo.cs`**

```csharp
namespace Mcp.ComputerUse.Core;

public readonly record struct Rect(int X, int Y, int Width, int Height);

public sealed record MonitorInfo(
    int Index,
    string DeviceName,
    Rect Bounds,
    Rect WorkArea,
    bool IsPrimary,
    int DpiX,
    int DpiY);
```

- [ ] **Step 2: Write failing integration test in `MonitorRegistryTests.cs`**

```csharp
using FluentAssertions;
using Mcp.ComputerUse.Core;
using Xunit;

namespace Mcp.ComputerUse.Tests;

public class MonitorRegistryTests
{
    [Fact]
    public void Refresh_returns_at_least_primary_monitor()
    {
        var reg = new MonitorRegistry();
        reg.Monitors.Should().NotBeEmpty();
        reg.Monitors.Should().Contain(m => m.IsPrimary);
        reg.Monitors[0].Bounds.Width.Should().BeGreaterThan(0);
        reg.Monitors[0].Bounds.Height.Should().BeGreaterThan(0);
        reg.Monitors[0].DpiX.Should().BeGreaterOrEqualTo(96);
    }
}
```

Run: `dotnet test --filter MonitorRegistryTests`
Expected: FAIL (MonitorRegistry not defined).

- [ ] **Step 3: Implement `MonitorRegistry.cs`**

```csharp
using System.Runtime.InteropServices;
using Mcp.ComputerUse.Native;

namespace Mcp.ComputerUse.Core;

public sealed class MonitorRegistry
{
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint MONITORINFOF_PRIMARY = 1;

    private readonly Lock _gate = new();
    public IReadOnlyList<MonitorInfo> Monitors { get; private set; } = [];

    public MonitorRegistry()
    {
        Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Refresh();
    }

    public void Refresh()
    {
        var list = new List<MonitorInfo>();
        int idx = 0;
        // Hold delegate in a local so the GC doesn't free it during the call.
        Win32.MonitorEnumProc proc = (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = string.Empty };
            if (!Win32.GetMonitorInfo(hMon, ref mi)) return true;
            Win32.GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY);
            list.Add(new MonitorInfo(
                Index: idx++,
                DeviceName: mi.szDevice,
                Bounds: new Rect(mi.rcMonitor.Left, mi.rcMonitor.Top,
                                 mi.rcMonitor.Right - mi.rcMonitor.Left,
                                 mi.rcMonitor.Bottom - mi.rcMonitor.Top),
                WorkArea: new Rect(mi.rcWork.Left, mi.rcWork.Top,
                                   mi.rcWork.Right - mi.rcWork.Left,
                                   mi.rcWork.Bottom - mi.rcWork.Top),
                IsPrimary: (mi.dwFlags & MONITORINFOF_PRIMARY) != 0,
                DpiX: (int)dpiX,
                DpiY: (int)dpiY));
            return true;
        };
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);

        lock (_gate)
        {
            Monitors = list;
        }
    }

    public MonitorInfo GetOrThrow(int index)
    {
        var snapshot = Monitors;
        if (index < 0 || index >= snapshot.Count)
        {
            Refresh();
            snapshot = Monitors;
        }
        if (index < 0 || index >= snapshot.Count)
            throw new ArgumentOutOfRangeException(nameof(index),
                $"Monitor index {index} not found. Available: 0..{snapshot.Count - 1}.");
        return snapshot[index];
    }
}
```

- [ ] **Step 4: Re-run tests**

Run: `dotnet test --filter MonitorRegistryTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add Mcp.ComputerUse/Core/MonitorInfo.cs Mcp.ComputerUse/Core/MonitorRegistry.cs Mcp.ComputerUse.Tests/MonitorRegistryTests.cs
git commit -m "feat(core): MonitorRegistry enumerates displays with per-monitor DPI"
```

### Task 1.3: CoordinateMapper

**Files:**
- Create: `Mcp.ComputerUse/Core/CoordinateMapper.cs`
- Create: `Mcp.ComputerUse.Tests/CoordinateMapperTests.cs`

- [ ] **Step 1: Write failing tests in `CoordinateMapperTests.cs`**

```csharp
using FluentAssertions;
using Mcp.ComputerUse.Core;
using Xunit;

namespace Mcp.ComputerUse.Tests;

public class CoordinateMapperTests
{
    private readonly CoordinateMapper _mapper = new();

    [Fact]
    public void PlanFor_4K_16x9_picks_FWXGA_close_ratio()
    {
        var plan = _mapper.PlanFor(3840, 2160, monitorLeft: 100, monitorTop: 50);
        plan.ScaledWidth.Should().Be(1366);
        plan.ScaledHeight.Should().Be(768);
        plan.FactorX.Should().BeApproximately(1366.0 / 3840.0, 1e-6);
        plan.MonitorLeft.Should().Be(100);
    }

    [Fact]
    public void PlanFor_WUXGA_16x10_picks_WXGA()
    {
        var plan = _mapper.PlanFor(1920, 1200, 0, 0);
        plan.ScaledWidth.Should().Be(1280);
        plan.ScaledHeight.Should().Be(800);
    }

    [Fact]
    public void PlanFor_below_target_does_not_upscale()
    {
        var plan = _mapper.PlanFor(800, 600, 0, 0);
        plan.ScaledWidth.Should().Be(800);
        plan.ScaledHeight.Should().Be(600);
        plan.FactorX.Should().Be(1.0);
    }

    [Fact]
    public void ModelToScreen_round_trips_via_ScreenToModel()
    {
        var plan = _mapper.PlanFor(3840, 2160, 1920, 0);
        var (sx, sy) = _mapper.ModelToScreen(plan, 640, 400);
        sx.Should().BeInRange(1920, 1920 + 3840);
        sy.Should().BeInRange(0, 2160);

        var (mx, my) = _mapper.ScreenToModel(plan, sx, sy);
        mx.Should().BeInRange(638, 642);
        my.Should().BeInRange(398, 402);
    }

    [Fact]
    public void ModelToScreen_rejects_out_of_bounds()
    {
        var plan = _mapper.PlanFor(3840, 2160, 0, 0);
        Action act = () => _mapper.ModelToScreen(plan, plan.ScaledWidth + 5, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
```

Run: `dotnet test --filter CoordinateMapperTests`
Expected: FAIL (CoordinateMapper not defined).

- [ ] **Step 2: Implement `CoordinateMapper.cs`**

```csharp
namespace Mcp.ComputerUse.Core;

public readonly record struct ScalingTarget(string Name, int Width, int Height);

public readonly record struct ScalePlan(
    int OrigWidth, int OrigHeight,
    int ScaledWidth, int ScaledHeight,
    double FactorX, double FactorY,
    int MonitorLeft, int MonitorTop,
    string TargetName);

public sealed class CoordinateMapper
{
    public static readonly ScalingTarget[] Targets =
    [
        new("XGA",   1024, 768),  // 4:3
        new("WXGA",  1280, 800),  // 16:10
        new("FWXGA", 1366, 768),  // ~16:9
    ];

    private const double AspectTolerance = 0.02;

    public ScalePlan PlanFor(int origW, int origH, int monitorLeft, int monitorTop)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(origW, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(origH, 0);

        double ratio = (double)origW / origH;
        ScalingTarget? best = null;
        foreach (var t in Targets)
        {
            double tRatio = (double)t.Width / t.Height;
            if (Math.Abs(tRatio - ratio) < AspectTolerance && t.Width < origW)
            {
                best = t;
                break;
            }
        }

        if (best is null)
            return new ScalePlan(origW, origH, origW, origH, 1.0, 1.0, monitorLeft, monitorTop, "NATIVE");

        var pick = best.Value;
        return new ScalePlan(
            origW, origH,
            pick.Width, pick.Height,
            (double)pick.Width / origW,
            (double)pick.Height / origH,
            monitorLeft, monitorTop,
            pick.Name);
    }

    public (int x, int y) ModelToScreen(ScalePlan p, int mx, int my)
    {
        if (mx < 0 || mx > p.ScaledWidth || my < 0 || my > p.ScaledHeight)
            throw new ArgumentOutOfRangeException(
                nameof(mx),
                $"Model coordinates ({mx},{my}) outside scaled bounds {p.ScaledWidth}x{p.ScaledHeight}.");
        return (
            (int)Math.Round(mx / p.FactorX) + p.MonitorLeft,
            (int)Math.Round(my / p.FactorY) + p.MonitorTop);
    }

    public (int x, int y) ScreenToModel(ScalePlan p, int sx, int sy) =>
        ((int)Math.Round((sx - p.MonitorLeft) * p.FactorX),
         (int)Math.Round((sy - p.MonitorTop) * p.FactorY));
}
```

- [ ] **Step 3: Re-run tests**

Run: `dotnet test --filter CoordinateMapperTests`
Expected: PASS (5/5).

- [ ] **Step 4: Commit**

```powershell
git add Mcp.ComputerUse/Core/CoordinateMapper.cs Mcp.ComputerUse.Tests/CoordinateMapperTests.cs
git commit -m "feat(core): CoordinateMapper for XGA/WXGA/FWXGA downscale + remap"
```

### Task 1.4: ScreenCaptureService

**Files:**
- Create: `Mcp.ComputerUse/Core/ScreenCaptureService.cs`
- Create: `Mcp.ComputerUse.Tests/ScreenCaptureSmokeTests.cs`

- [ ] **Step 1: Write `ScreenCaptureService.cs`**

```csharp
using System.Runtime.InteropServices;
using Mcp.ComputerUse.Native;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mcp.ComputerUse.Core;

public sealed class ScreenCaptureService
{
    private readonly MonitorRegistry _registry;
    private readonly CoordinateMapper _mapper;

    public ScreenCaptureService(MonitorRegistry registry, CoordinateMapper mapper)
    {
        _registry = registry;
        _mapper = mapper;
    }

    public sealed record CaptureResult(byte[] PngBytes, ScalePlan Plan, int OrigWidth, int OrigHeight);

    public CaptureResult CaptureMonitor(int monitorIndex, bool downscale, bool grayscale)
    {
        var monitor = _registry.GetOrThrow(monitorIndex);
        int w = monitor.Bounds.Width;
        int h = monitor.Bounds.Height;

        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, bmp = IntPtr.Zero, oldBmp = IntPtr.Zero;
        try
        {
            screenDc = Win32.GetDC(IntPtr.Zero);
            memDc = Win32.CreateCompatibleDC(screenDc);
            bmp = Win32.CreateCompatibleBitmap(screenDc, w, h);
            oldBmp = Win32.SelectObject(memDc, bmp);
            if (!Win32.BitBlt(memDc, 0, 0, w, h, screenDc, monitor.Bounds.X, monitor.Bounds.Y, Win32.SRCCOPY | Win32.CAPTUREBLT))
                throw new InvalidOperationException($"BitBlt failed for monitor {monitorIndex}.");

            byte[] pixels = ReadBgra(memDc, bmp, w, h);
            using var img = Image.LoadPixelData<Bgra32>(pixels, w, h);

            var plan = downscale
                ? _mapper.PlanFor(w, h, monitor.Bounds.X, monitor.Bounds.Y)
                : new ScalePlan(w, h, w, h, 1.0, 1.0, monitor.Bounds.X, monitor.Bounds.Y, "NATIVE");

            if (downscale && (plan.ScaledWidth != w || plan.ScaledHeight != h))
                img.Mutate(c => c.Resize(plan.ScaledWidth, plan.ScaledHeight, KnownResamplers.Lanczos3));

            if (grayscale)
                img.Mutate(c => c.Grayscale(GrayscaleMode.Bt709));

            using var ms = new MemoryStream();
            img.Save(ms, new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression });
            return new CaptureResult(ms.ToArray(), plan, w, h);
        }
        finally
        {
            if (oldBmp != IntPtr.Zero && memDc != IntPtr.Zero) Win32.SelectObject(memDc, oldBmp);
            if (bmp != IntPtr.Zero) Win32.DeleteObject(bmp);
            if (memDc != IntPtr.Zero) Win32.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static byte[] ReadBgra(IntPtr memDc, IntPtr bmp, int w, int h)
    {
        var bmi = new BITMAPINFO
        {
            bmiColors = new byte[1024],
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            }
        };

        int stride = w * 4;
        var buffer = new byte[stride * h];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            Win32.GetDIBits(memDc, bmp, 0, (uint)h, handle.AddrOfPinnedObject(), ref bmi, Win32.DIB_RGB_COLORS);
        }
        finally { handle.Free(); }
        return buffer;
    }
}
```

- [ ] **Step 2: Write smoke test `ScreenCaptureSmokeTests.cs`**

```csharp
using FluentAssertions;
using Mcp.ComputerUse.Core;
using SixLabors.ImageSharp;
using Xunit;

namespace Mcp.ComputerUse.Tests;

public class ScreenCaptureSmokeTests
{
    [Fact]
    public void CaptureMonitor_primary_produces_valid_png_at_wxga()
    {
        var reg = new MonitorRegistry();
        var mapper = new CoordinateMapper();
        var svc = new ScreenCaptureService(reg, mapper);

        var primaryIdx = reg.Monitors.Single(m => m.IsPrimary).Index;
        var result = svc.CaptureMonitor(primaryIdx, downscale: true, grayscale: false);

        result.PngBytes.Should().NotBeNullOrEmpty();
        result.PngBytes.Length.Should().BeGreaterThan(1000);

        // Verify it's a real PNG and dimensions match the scale plan.
        using var img = Image.Load(result.PngBytes);
        img.Width.Should().Be(result.Plan.ScaledWidth);
        img.Height.Should().Be(result.Plan.ScaledHeight);
    }
}
```

- [ ] **Step 3: Run test**

Run: `dotnet test --filter ScreenCaptureSmokeTests`
Expected: PASS (requires an interactive desktop).

- [ ] **Step 4: Commit**

```powershell
git add Mcp.ComputerUse/Core/ScreenCaptureService.cs Mcp.ComputerUse.Tests/ScreenCaptureSmokeTests.cs
git commit -m "feat(core): ScreenCaptureService captures monitor via BitBlt + downscale via ImageSharp"
```

### Task 1.5: ScreenshotResult DTO + Plan cache

**Files:**
- Modify: `Mcp.ComputerUse/Json/McpJsonContext.cs`
- Create: `Mcp.ComputerUse/Core/ScalePlanCache.cs`

- [ ] **Step 1: Write `ScalePlanCache.cs`**

```csharp
using System.Collections.Concurrent;

namespace Mcp.ComputerUse.Core;

public sealed class ScalePlanCache
{
    private readonly ConcurrentDictionary<int, ScalePlan> _byMonitor = new();
    public void Set(int monitorIndex, ScalePlan plan) => _byMonitor[monitorIndex] = plan;
    public ScalePlan? Get(int monitorIndex) => _byMonitor.TryGetValue(monitorIndex, out var p) ? p : null;
}
```

- [ ] **Step 2: Replace `McpJsonContext.cs` with full DTO set**

```csharp
using System.Text.Json.Serialization;
using Mcp.ComputerUse.Core;

namespace Mcp.ComputerUse.Json;

public sealed record PingResult(string Message, long ServerTimeUnixMs);

public sealed record MonitorDto(int Index, string DeviceName, Rect Bounds, Rect WorkArea, bool IsPrimary, int DpiX, int DpiY);
public sealed record ListMonitorsResult(IReadOnlyList<MonitorDto> Monitors);

public sealed record ScreenshotMeta(
    int MonitorIndex,
    int OrigWidth,
    int OrigHeight,
    int ScaledWidth,
    int ScaledHeight,
    double FactorX,
    double FactorY,
    int MonitorLeft,
    int MonitorTop,
    string TargetName,
    string? SavedTo);

public sealed record OkResult(bool Ok = true);
public sealed record CursorPositionResult(int X, int Y, int MonitorIndex);
public sealed record LaunchAppResult(int Pid);
public sealed record ShellResult(int ExitCode, string Stdout, string Stderr);
public sealed record ReadFileResult(string Content, string Encoding);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PingResult))]
[JsonSerializable(typeof(MonitorDto))]
[JsonSerializable(typeof(ListMonitorsResult))]
[JsonSerializable(typeof(ScreenshotMeta))]
[JsonSerializable(typeof(OkResult))]
[JsonSerializable(typeof(CursorPositionResult))]
[JsonSerializable(typeof(LaunchAppResult))]
[JsonSerializable(typeof(ShellResult))]
[JsonSerializable(typeof(ReadFileResult))]
[JsonSerializable(typeof(Rect))]
internal partial class McpJsonContext : JsonSerializerContext;
```

- [ ] **Step 3: Build**

Run: `dotnet build -c Release`
Expected: succeeds.

- [ ] **Step 4: Commit**

```powershell
git add Mcp.ComputerUse/Json/McpJsonContext.cs Mcp.ComputerUse/Core/ScalePlanCache.cs
git commit -m "feat: DTOs in JsonSerializerContext + per-monitor ScalePlan cache"
```

### Task 1.6: ScreenshotStorage + MonitorTools + ScreenTools

**Files:**
- Create: `Mcp.ComputerUse/Core/ScreenshotStorage.cs`
- Create: `Mcp.ComputerUse/Tools/MonitorTools.cs`
- Create: `Mcp.ComputerUse/Tools/ScreenTools.cs`
- Modify: `Mcp.ComputerUse/Program.cs`

- [ ] **Step 1: Write `ScreenshotStorage.cs`**

```csharp
namespace Mcp.ComputerUse.Core;

public sealed class ScreenshotStorage
{
    public string DefaultDir { get; }

    public ScreenshotStorage()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MCP_COMPUTERUSE_SCREENSHOTS_DIR");
        DefaultDir = !string.IsNullOrWhiteSpace(fromEnv)
            ? Path.GetFullPath(fromEnv)
            : Environment.CurrentDirectory;
    }

    public string Save(byte[] png, int monitorIndex, string? overridePath)
    {
        var dir = !string.IsNullOrWhiteSpace(overridePath) ? Path.GetFullPath(overridePath) : DefaultDir;
        Directory.CreateDirectory(dir);
        var filename = $"screenshot-mon{monitorIndex}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.png";
        var fullPath = Path.Combine(dir, filename);
        File.WriteAllBytes(fullPath, png);
        return fullPath;
    }
}
```

- [ ] **Step 2: Write `MonitorTools.cs`**

```csharp
using System.ComponentModel;
using Mcp.ComputerUse.Core;
using Mcp.ComputerUse.Json;
using ModelContextProtocol.Server;

namespace Mcp.ComputerUse.Tools;

[McpServerToolType]
public sealed class MonitorTools
{
    private readonly MonitorRegistry _registry;
    public MonitorTools(MonitorRegistry registry) => _registry = registry;

    [McpServerTool, Description("Enumerate connected monitors with bounds, work area, primary flag, and per-monitor DPI. Call this first to discover monitor indices.")]
    public ListMonitorsResult ListMonitors()
    {
        _registry.Refresh();
        var dtos = _registry.Monitors
            .Select(m => new MonitorDto(m.Index, m.DeviceName, m.Bounds, m.WorkArea, m.IsPrimary, m.DpiX, m.DpiY))
            .ToList();
        return new ListMonitorsResult(dtos);
    }
}
```

- [ ] **Step 3: Write `ScreenTools.cs`**

```csharp
using System.ComponentModel;
using Mcp.ComputerUse.Core;
using Mcp.ComputerUse.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mcp.ComputerUse.Tools;

[McpServerToolType]
public sealed class ScreenTools
{
    private readonly ScreenCaptureService _capture;
    private readonly ScalePlanCache _planCache;
    private readonly ScreenshotStorage _storage;

    public ScreenTools(ScreenCaptureService capture, ScalePlanCache planCache, ScreenshotStorage storage)
    {
        _capture = capture;
        _planCache = planCache;
        _storage = storage;
    }

    [McpServerTool, Description("Capture a screenshot of the specified monitor. Returns both an image block (PNG, base64) for vision and a text block with the ScalePlan metadata. The model should use the scaled coordinates returned here for subsequent mouse_* calls with coord_space='model'.")]
    public CallToolResult Screenshot(
        [Description("Monitor index from list_monitors. 0 is typically primary.")] int monitorIndex,
        [Description("Downscale to XGA/WXGA/FWXGA (default true). Set false for 1:1 pixel coords.")] bool downscale = true,
        [Description("Convert to grayscale to reduce size (default false).")] bool grayscale = false,
        [Description("Optional override directory to save the PNG. Defaults to current working directory or MCP_COMPUTERUSE_SCREENSHOTS_DIR.")] string? savePath = null)
    {
        var result = _capture.CaptureMonitor(monitorIndex, downscale, grayscale);
        _planCache.Set(monitorIndex, result.Plan);
        var saved = _storage.Save(result.PngBytes, monitorIndex, savePath);

        var meta = new ScreenshotMeta(
            MonitorIndex: monitorIndex,
            OrigWidth: result.OrigWidth,
            OrigHeight: result.OrigHeight,
            ScaledWidth: result.Plan.ScaledWidth,
            ScaledHeight: result.Plan.ScaledHeight,
            FactorX: result.Plan.FactorX,
            FactorY: result.Plan.FactorY,
            MonitorLeft: result.Plan.MonitorLeft,
            MonitorTop: result.Plan.MonitorTop,
            TargetName: result.Plan.TargetName,
            SavedTo: saved);

        return new CallToolResult
        {
            Content =
            [
                new ImageContentBlock { Data = Convert.ToBase64String(result.PngBytes), MimeType = "image/png" },
                new TextContentBlock { Text = System.Text.Json.JsonSerializer.Serialize(meta, McpJsonContext.Default.ScreenshotMeta) }
            ]
        };
    }
}
```

> Note: exact namespaces for `CallToolResult` / `ImageContentBlock` / `TextContentBlock` may differ slightly across MCP SDK minor versions. If a type isn't found, search the SDK with `dotnet sln list` or grep the `ModelContextProtocol` package contents — the equivalents in 1.3.0 are guaranteed to exist; just adjust the `using`. Implementer: verify with the IDE's go-to-definition before falling back to `Content` block construction.

- [ ] **Step 4: Update `Program.cs` to register services + tools**

```csharp
using Mcp.ComputerUse.Core;
using Mcp.ComputerUse.Native;
using Mcp.ComputerUse.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Defensive: ensure PerMonitorV2 even before manifest is honored.
Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services
    .AddSingleton<MonitorRegistry>()
    .AddSingleton<CoordinateMapper>()
    .AddSingleton<ScalePlanCache>()
    .AddSingleton<ScreenshotStorage>()
    .AddSingleton<ScreenCaptureService>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<PingTools>()
    .WithTools<MonitorTools>()
    .WithTools<ScreenTools>();

await builder.Build().RunAsync();
```

- [ ] **Step 5: Build + AOT publish**

Run: `dotnet publish Mcp.ComputerUse/Mcp.ComputerUse.csproj -c Release -r win-x64`
Expected: produces single `mcp-computeruse.exe` < 25 MB.

- [ ] **Step 6: Manual E2E from Claude Code**

In Claude Code with the server configured: ask "list my monitors". Expected: tool result with each monitor. Then ask "take a screenshot of monitor 0". Expected: Claude Code shows the PNG inline and includes the screenshot metadata; PNG file saved to the cwd of the Claude Code session.

- [ ] **Step 7: Commit**

```powershell
git add Mcp.ComputerUse/Core/ScreenshotStorage.cs Mcp.ComputerUse/Tools/MonitorTools.cs Mcp.ComputerUse/Tools/ScreenTools.cs Mcp.ComputerUse/Program.cs
git commit -m "feat(tools): list_monitors and screenshot with image content block"
```

---

## Chunk 2: Input Layer (Mouse + Keyboard)

Goal: every `mouse_*` and `key_*` tool works from Claude Code. Coordinate remap via cached ScalePlan is validated end-to-end.

### Task 2.1: InputService — mouse foundations

**Files:**
- Create: `Mcp.ComputerUse/Core/InputService.cs`
- Create: `Mcp.ComputerUse/Core/MouseButton.cs`

- [ ] **Step 1: Write `MouseButton.cs`**

```csharp
namespace Mcp.ComputerUse.Core;

public enum MouseButton { Left, Right, Middle }
```

- [ ] **Step 2: Write `InputService.cs` (mouse only — keyboard in next task)**

```csharp
using System.Runtime.InteropServices;
using Mcp.ComputerUse.Native;

namespace Mcp.ComputerUse.Core;

public sealed class InputService
{
    public void MouseMoveScreen(int sx, int sy)
    {
        var (nx, ny) = NormalizeToVirtualDesktop(sx, sy);
        var inputs = new[]
        {
            new INPUT
            {
                type = Win32.INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = nx, dy = ny,
                        dwFlags = Win32.MOUSEEVENTF_MOVE | Win32.MOUSEEVENTF_ABSOLUTE | Win32.MOUSEEVENTF_VIRTUALDESK,
                    }
                }
            }
        };
        SendOrThrow(inputs);
    }

    public void MouseClickScreen(int sx, int sy, MouseButton button, int clicks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(clicks, 1);
        MouseMoveScreen(sx, sy);
        var (down, up) = ButtonFlags(button);
        var list = new List<INPUT>(clicks * 2);
        for (int i = 0; i < clicks; i++)
        {
            list.Add(MouseFlag(down));
            list.Add(MouseFlag(up));
        }
        SendOrThrow(list.ToArray());
    }

    public void MouseDownScreen(int sx, int sy, MouseButton button)
    {
        MouseMoveScreen(sx, sy);
        var (down, _) = ButtonFlags(button);
        SendOrThrow([MouseFlag(down)]);
    }

    public void MouseUpScreen(int sx, int sy, MouseButton button)
    {
        MouseMoveScreen(sx, sy);
        var (_, up) = ButtonFlags(button);
        SendOrThrow([MouseFlag(up)]);
    }

    public void MouseDragScreen(int fromX, int fromY, int toX, int toY, MouseButton button)
    {
        MouseDownScreen(fromX, fromY, button);
        MouseMoveScreen(toX, toY);
        MouseUpScreen(toX, toY, button);
    }

    public void MouseScrollScreen(int sx, int sy, int clicks, bool horizontal)
    {
        MouseMoveScreen(sx, sy);
        uint flag = horizontal ? Win32.MOUSEEVENTF_HWHEEL : Win32.MOUSEEVENTF_WHEEL;
        SendOrThrow([new INPUT
        {
            type = Win32.INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    mouseData = (uint)(clicks * Win32.WHEEL_DELTA),
                    dwFlags = flag,
                }
            }
        }]);
    }

    public (int x, int y) GetCursorPos()
    {
        if (!Win32.GetCursorPos(out var p))
            throw new InvalidOperationException("GetCursorPos failed");
        return (p.X, p.Y);
    }

    private static (uint down, uint up) ButtonFlags(MouseButton b) => b switch
    {
        MouseButton.Left => (Win32.MOUSEEVENTF_LEFTDOWN, Win32.MOUSEEVENTF_LEFTUP),
        MouseButton.Right => (Win32.MOUSEEVENTF_RIGHTDOWN, Win32.MOUSEEVENTF_RIGHTUP),
        MouseButton.Middle => (Win32.MOUSEEVENTF_MIDDLEDOWN, Win32.MOUSEEVENTF_MIDDLEUP),
        _ => throw new ArgumentOutOfRangeException(nameof(b)),
    };

    private static INPUT MouseFlag(uint flag) => new()
    {
        type = Win32.INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flag } }
    };

    private static (int nx, int ny) NormalizeToVirtualDesktop(int sx, int sy)
    {
        int vsLeft = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int vsTop = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int vsW = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        int vsH = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);
        int nx = (int)Math.Round((sx - vsLeft) * 65535.0 / Math.Max(1, vsW - 1));
        int ny = (int)Math.Round((sy - vsTop) * 65535.0 / Math.Max(1, vsH - 1));
        return (nx, ny);
    }

    private static void SendOrThrow(INPUT[] inputs)
    {
        uint sent = Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            throw new InvalidOperationException($"SendInput sent {sent} of {inputs.Length} events. GetLastError={Marshal.GetLastWin32Error()}.");
    }

    // Keyboard methods added in Task 2.2.
    internal INPUT MakeUnicodeDown(ushort unit) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wScan = unit, dwFlags = Win32.KEYEVENTF_UNICODE } }
    };
    internal INPUT MakeUnicodeUp(ushort unit) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wScan = unit, dwFlags = Win32.KEYEVENTF_UNICODE | Win32.KEYEVENTF_KEYUP } }
    };
    internal static void SendBatch(INPUT[] inputs) => SendOrThrow(inputs);
}
```

- [ ] **Step 3: Build**

Run: `dotnet build -c Release`
Expected: succeeds.

- [ ] **Step 4: Commit**

```powershell
git add Mcp.ComputerUse/Core/InputService.cs Mcp.ComputerUse/Core/MouseButton.cs
git commit -m "feat(core): InputService mouse (move/click/down/up/drag/scroll)"
```

### Task 2.2: InputService — keyboard

**Files:**
- Modify: `Mcp.ComputerUse/Core/InputService.cs`
- Create: `Mcp.ComputerUse/Core/VirtualKeyMap.cs`

- [ ] **Step 1: Write `VirtualKeyMap.cs`**

```csharp
namespace Mcp.ComputerUse.Core;

public static class VirtualKeyMap
{
    private static readonly Dictionary<string, ushort> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["return"] = 0x0D,
        ["tab"] = 0x09, ["backspace"] = 0x08, ["escape"] = 0x1B, ["esc"] = 0x1B,
        ["space"] = 0x20, ["pgup"] = 0x21, ["pgdn"] = 0x22, ["end"] = 0x23, ["home"] = 0x24,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        ["insert"] = 0x2D, ["delete"] = 0x2E, ["del"] = 0x2E,
        ["win"] = 0x5B, ["lwin"] = 0x5B, ["rwin"] = 0x5C,
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12,
        ["f1"] = 0x70, ["f2"] = 0x71, ["f3"] = 0x72, ["f4"] = 0x73, ["f5"] = 0x74, ["f6"] = 0x75,
        ["f7"] = 0x76, ["f8"] = 0x77, ["f9"] = 0x78, ["f10"] = 0x79, ["f11"] = 0x7A, ["f12"] = 0x7B,
        ["capslock"] = 0x14, ["numlock"] = 0x90, ["scrolllock"] = 0x91,
        ["printscreen"] = 0x2C, ["prtsc"] = 0x2C,
    };

    public static ushort Resolve(string key)
    {
        if (Map.TryGetValue(key, out var vk)) return vk;
        if (key.Length == 1)
        {
            char ch = char.ToUpperInvariant(key[0]);
            if (ch is (>= '0' and <= '9') or (>= 'A' and <= 'Z')) return ch;
            // Fall through to VkKeyScanW for layout-dependent symbols
        }
        if (key.Length == 1)
        {
            ushort scan = Native.Win32.VkKeyScanW(key[0]);
            return (ushort)(scan & 0xFF);
        }
        throw new ArgumentException($"Unknown key name: '{key}'.", nameof(key));
    }

    public static ushort[] ParseChord(string chord)
    {
        // "ctrl+shift+esc"
        return chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(Resolve)
                    .ToArray();
    }
}
```

- [ ] **Step 2: Append keyboard methods to `InputService.cs`**

Add these methods inside the `InputService` class:

```csharp
public void TypeText(string text, int delayMs)
{
    ArgumentNullException.ThrowIfNull(text);
    foreach (var unit in text)
    {
        SendBatch([MakeUnicodeDown(unit), MakeUnicodeUp(unit)]);
        if (delayMs > 0) Thread.Sleep(delayMs);
    }
}

public void KeyPress(string key)
{
    var vk = VirtualKeyMap.Resolve(key);
    SendBatch([VkDown(vk), VkUp(vk)]);
}

public void KeyHold(string key, int ms)
{
    var vk = VirtualKeyMap.Resolve(key);
    SendBatch([VkDown(vk)]);
    Thread.Sleep(ms);
    SendBatch([VkUp(vk)]);
}

public void KeyHotkey(string chord)
{
    var vks = VirtualKeyMap.ParseChord(chord);
    if (vks.Length == 0) return;
    var down = vks.Select(VkDown).ToArray();
    var up = vks.Reverse().Select(VkUp).ToArray();
    SendBatch(down);
    SendBatch(up);
}

private static INPUT VkDown(ushort vk) => new()
{
    type = Win32.INPUT_KEYBOARD,
    U = new InputUnion { ki = new KEYBDINPUT { wVk = vk } }
};

private static INPUT VkUp(ushort vk) => new()
{
    type = Win32.INPUT_KEYBOARD,
    U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = Win32.KEYEVENTF_KEYUP } }
};
```

- [ ] **Step 3: Build**

Run: `dotnet build -c Release`
Expected: succeeds.

- [ ] **Step 4: Commit**

```powershell
git add Mcp.ComputerUse/Core/InputService.cs Mcp.ComputerUse/Core/VirtualKeyMap.cs
git commit -m "feat(core): InputService keyboard (type_text/key_press/key_hold/key_hotkey)"
```

### Task 2.3: MouseTools

**Files:**
- Create: `Mcp.ComputerUse/Tools/MouseTools.cs`
- Modify: `Mcp.ComputerUse/Program.cs`

- [ ] **Step 1: Write `MouseTools.cs`**

```csharp
using System.ComponentModel;
using Mcp.ComputerUse.Core;
using Mcp.ComputerUse.Json;
using ModelContextProtocol.Server;

namespace Mcp.ComputerUse.Tools;

[McpServerToolType]
public sealed class MouseTools
{
    private readonly InputService _input;
    private readonly ScalePlanCache _planCache;
    private readonly CoordinateMapper _mapper;
    private readonly MonitorRegistry _registry;

    public MouseTools(InputService input, ScalePlanCache planCache, CoordinateMapper mapper, MonitorRegistry registry)
    {
        _input = input;
        _planCache = planCache;
        _mapper = mapper;
        _registry = registry;
    }

    [McpServerTool, Description("Move the cursor to (x,y). coord_space='model' uses the scaled coords from the last screenshot of this monitor (default). coord_space='screen' uses physical desktop pixels.")]
    public OkResult MouseMove(
        [Description("Monitor index from list_monitors.")] int monitorIndex,
        [Description("X coordinate")] int x,
        [Description("Y coordinate")] int y,
        [Description("'model' (default) or 'screen'.")] string coordSpace = "model")
    {
        var (sx, sy) = ToScreen(monitorIndex, x, y, coordSpace);
        _input.MouseMoveScreen(sx, sy);
        return new OkResult();
    }

    [McpServerTool, Description("Click at (x,y). button: left|right|middle. clicks: 1 (single), 2 (double), 3 (triple).")]
    public OkResult MouseClick(int monitorIndex, int x, int y, string button = "left", int clicks = 1, string coordSpace = "model")
    {
        var (sx, sy) = ToScreen(monitorIndex, x, y, coordSpace);
        _input.MouseClickScreen(sx, sy, ParseButton(button), clicks);
        return new OkResult();
    }

    [McpServerTool, Description("Press a mouse button without releasing.")]
    public OkResult MouseDown(int monitorIndex, int x, int y, string button = "left", string coordSpace = "model")
    {
        var (sx, sy) = ToScreen(monitorIndex, x, y, coordSpace);
        _input.MouseDownScreen(sx, sy, ParseButton(button));
        return new OkResult();
    }

    [McpServerTool, Description("Release a previously pressed mouse button.")]
    public OkResult MouseUp(int monitorIndex, int x, int y, string button = "left", string coordSpace = "model")
    {
        var (sx, sy) = ToScreen(monitorIndex, x, y, coordSpace);
        _input.MouseUpScreen(sx, sy, ParseButton(button));
        return new OkResult();
    }

    [McpServerTool, Description("Drag with the specified button held from (fromX, fromY) to (toX, toY).")]
    public OkResult MouseDrag(int monitorIndex, int fromX, int fromY, int toX, int toY, string button = "left", string coordSpace = "model")
    {
        var (sx1, sy1) = ToScreen(monitorIndex, fromX, fromY, coordSpace);
        var (sx2, sy2) = ToScreen(monitorIndex, toX, toY, coordSpace);
        _input.MouseDragScreen(sx1, sy1, sx2, sy2, ParseButton(button));
        return new OkResult();
    }

    [McpServerTool, Description("Scroll the wheel by N clicks (positive = up/right, negative = down/left). direction: vertical|horizontal.")]
    public OkResult MouseScroll(int monitorIndex, int x, int y, int clicks, string direction = "vertical", string coordSpace = "model")
    {
        var (sx, sy) = ToScreen(monitorIndex, x, y, coordSpace);
        _input.MouseScrollScreen(sx, sy, clicks, horizontal: direction.Equals("horizontal", StringComparison.OrdinalIgnoreCase));
        return new OkResult();
    }

    [McpServerTool, Description("Return current cursor position in MODEL coordinates for the given monitor (using its last ScalePlan). If no screenshot has been taken, returns screen-coords.")]
    public CursorPositionResult CursorPosition(int monitorIndex)
    {
        var (sx, sy) = _input.GetCursorPos();
        var plan = _planCache.Get(monitorIndex);
        if (plan is null) return new CursorPositionResult(sx, sy, monitorIndex);
        var (mx, my) = _mapper.ScreenToModel(plan.Value, sx, sy);
        return new CursorPositionResult(mx, my, monitorIndex);
    }

    private (int sx, int sy) ToScreen(int monitorIndex, int x, int y, string coordSpace)
    {
        if (coordSpace.Equals("screen", StringComparison.OrdinalIgnoreCase))
            return (x, y);

        var plan = _planCache.Get(monitorIndex)
            ?? throw new InvalidOperationException(
                $"No ScalePlan cached for monitor {monitorIndex}. Call screenshot first, or pass coord_space='screen'.");
        return _mapper.ModelToScreen(plan, x, y);
    }

    private static MouseButton ParseButton(string s) => s.ToLowerInvariant() switch
    {
        "left" => MouseButton.Left,
        "right" => MouseButton.Right,
        "middle" => MouseButton.Middle,
        _ => throw new ArgumentException($"Unknown button '{s}'.", nameof(s)),
    };
}
```

- [ ] **Step 2: Register in `Program.cs`**

Add to the services chain:
```csharp
    .AddSingleton<InputService>()
```
Add to the MCP chain:
```csharp
    .WithTools<MouseTools>()
```

- [ ] **Step 3: Build + AOT publish**

Run: `dotnet publish Mcp.ComputerUse/Mcp.ComputerUse.csproj -c Release -r win-x64`
Expected: succeeds, < 25 MB.

- [ ] **Step 4: Commit**

```powershell
git add Mcp.ComputerUse/Tools/MouseTools.cs Mcp.ComputerUse/Program.cs
git commit -m "feat(tools): mouse_move/click/down/up/drag/scroll/cursor_position with coord remap"
```

### Task 2.4: KeyboardTools + wait

**Files:**
- Create: `Mcp.ComputerUse/Tools/KeyboardTools.cs`
- Modify: `Mcp.ComputerUse/Program.cs`

- [ ] **Step 1: Write `KeyboardTools.cs`**

```csharp
using System.ComponentModel;
using Mcp.ComputerUse.Core;
using Mcp.ComputerUse.Json;
using ModelContextProtocol.Server;

namespace Mcp.ComputerUse.Tools;

[McpServerToolType]
public sealed class KeyboardTools
{
    private readonly InputService _input;
    public KeyboardTools(InputService input) => _input = input;

    [McpServerTool, Description("Type Unicode text via KEYEVENTF_UNICODE — independent of keyboard layout. delay_ms throttles between characters.")]
    public OkResult TypeText(
        [Description("Text to type.")] string text,
        [Description("Delay in ms between characters. 0 = no delay.")] int delayMs = 0)
    {
        _input.TypeText(text, delayMs);
        return new OkResult();
    }

    [McpServerTool, Description("Press and release a single key. Examples: 'Enter', 'F4', 'a', 'Escape'.")]
    public OkResult KeyPress(string key)
    {
        _input.KeyPress(key);
        return new OkResult();
    }

    [McpServerTool, Description("Hold a key down for the specified duration in ms, then release.")]
    public OkResult KeyHold(string key, int ms)
    {
        _input.KeyHold(key, ms);
        return new OkResult();
    }

    [McpServerTool, Description("Press a chord such as 'ctrl+shift+esc'. All modifiers go down in order, then up in reverse order.")]
    public OkResult KeyHotkey([Description("Chord string with '+' separators.")] string keys)
    {
        _input.KeyHotkey(keys);
        return new OkResult();
    }

    [McpServerTool, Description("Pause for the specified number of milliseconds. Useful between UI actions to let animations settle.")]
    public OkResult Wait(int ms)
    {
        if (ms > 0) Thread.Sleep(ms);
        return new OkResult();
    }
}
```

- [ ] **Step 2: Register in `Program.cs`**

Append `.WithTools<KeyboardTools>()`.

- [ ] **Step 3: Build**

Run: `dotnet build -c Release`
Expected: succeeds.

- [ ] **Step 4: Manual E2E from Claude Code**

Ask: "open Notepad on monitor 0 via launch_app then type Hello World and press Ctrl+S then type C:\\tmp\\hi.txt then press Enter."
Expected: end-to-end works. (Note: launch_app comes in Chunk 3 — until then, manually open Notepad first and use just typing/hotkeys.)

Intermediate verification (without launch_app): manually open Notepad, then ask Claude: "take a screenshot of monitor 0, click in the center, then type 'Hello'". Expected: text appears in Notepad.

- [ ] **Step 5: Commit**

```powershell
git add Mcp.ComputerUse/Tools/KeyboardTools.cs Mcp.ComputerUse/Program.cs
git commit -m "feat(tools): type_text/key_press/key_hold/key_hotkey/wait"
```

---

## Chunk 3: Files, Launch, Shell

Goal: tools for reading/writing files, creating folders, launching processes, and running PowerShell. Each is a thin wrapper over BCL.

### Task 3.1: FileService + FileTools

**Files:**
- Create: `Mcp.ComputerUse/Core/FileService.cs`
- Create: `Mcp.ComputerUse/Tools/FileTools.cs`
- Modify: `Mcp.ComputerUse/Program.cs`
- Create: `Mcp.ComputerUse.Tests/FileServiceTests.cs`

- [ ] **Step 1: Write `FileService.cs`**

```csharp
using System.Diagnostics;
using System.Text;

namespace Mcp.ComputerUse.Core;

public sealed class FileService
{
    public string ReadFile(string path, string encoding)
    {
        var full = Path.GetFullPath(path);
        if (encoding.Equals("binary", StringComparison.OrdinalIgnoreCase))
            return Convert.ToBase64String(File.ReadAllBytes(full));
        return File.ReadAllText(full, ResolveEncoding(encoding));
    }

    public void WriteFile(string path, string content, string encoding, bool overwrite)
    {
        var full = Path.GetFullPath(path);
        if (!overwrite && File.Exists(full))
            throw new IOException($"File '{full}' already exists. Pass overwrite=true to replace.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (encoding.Equals("binary", StringComparison.OrdinalIgnoreCase))
            File.WriteAllBytes(full, Convert.FromBase64String(content));
        else
            File.WriteAllText(full, content, ResolveEncoding(encoding));
    }

    public void CreateFolder(string path) => Directory.CreateDirectory(Path.GetFullPath(path));

    public int LaunchApp(string path, string? args, string? workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = path,
            Arguments = args ?? string.Empty,
            WorkingDirectory = workingDir ?? string.Empty,
            UseShellExecute = true,
        };
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to launch '{path}'.");
        return p.Id;
    }

    public (int exitCode, string stdout, string stderr) Shell(string command, string? workingDir, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -NonInteractive -Command \"{command.Replace("\"", "\\\"")}\"",
            WorkingDirectory = workingDir ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start powershell.");
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new TimeoutException($"Shell command timed out after {timeoutMs}ms.");
        }
        return (p.ExitCode, p.StandardOutput.ReadToEnd(), p.StandardError.ReadToEnd());
    }

    private static Encoding ResolveEncoding(string name) => name.ToLowerInvariant() switch
    {
        "utf8" or "utf-8" => Encoding.UTF8,
        "ascii" => Encoding.ASCII,
        "utf16" or "utf-16" or "unicode" => Encoding.Unicode,
        _ => throw new ArgumentException($"Unknown encoding '{name}'."),
    };
}
```

- [ ] **Step 2: Write `FileServiceTests.cs`**

```csharp
using FluentAssertions;
using Mcp.ComputerUse.Core;
using Xunit;

namespace Mcp.ComputerUse.Tests;

public class FileServiceTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mcp-cu-tests-" + Guid.NewGuid());

    public FileServiceTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, recursive: true); } catch { } }

    [Fact]
    public void WriteFile_then_ReadFile_round_trips_utf8()
    {
        var svc = new FileService();
        var path = Path.Combine(_tmp, "hello.txt");
        svc.WriteFile(path, "Привет, мир", "utf8", overwrite: false);
        svc.ReadFile(path, "utf8").Should().Be("Привет, мир");
    }

    [Fact]
    public void WriteFile_refuses_overwrite_by_default()
    {
        var svc = new FileService();
        var path = Path.Combine(_tmp, "x.txt");
        svc.WriteFile(path, "a", "utf8", overwrite: false);
        Action act = () => svc.WriteFile(path, "b", "utf8", overwrite: false);
        act.Should().Throw<IOException>();
        svc.WriteFile(path, "b", "utf8", overwrite: true);
        svc.ReadFile(path, "utf8").Should().Be("b");
    }

    [Fact]
    public void Binary_round_trips_via_base64()
    {
        var svc = new FileService();
        var path = Path.Combine(_tmp, "blob.bin");
        var data = new byte[] { 0, 1, 2, 3, 254, 255 };
        svc.WriteFile(path, Convert.ToBase64String(data), "binary", overwrite: false);
        var read = svc.ReadFile(path, "binary");
        Convert.FromBase64String(read).Should().Equal(data);
    }
}
```

- [ ] **Step 3: Run tests**

Run: `dotnet test --filter FileServiceTests`
Expected: 3 passed.

- [ ] **Step 4: Write `FileTools.cs`**

```csharp
using System.ComponentModel;
using Mcp.ComputerUse.Core;
using Mcp.ComputerUse.Json;
using ModelContextProtocol.Server;

namespace Mcp.ComputerUse.Tools;

[McpServerToolType]
public sealed class FileTools
{
    private readonly FileService _files;
    public FileTools(FileService files) => _files = files;

    [McpServerTool, Description("Read a file. encoding: utf8|ascii|utf16|binary. binary returns base64.")]
    public ReadFileResult ReadFile(string path, string encoding = "utf8")
        => new(_files.ReadFile(path, encoding), encoding);

    [McpServerTool, Description("Write a file. encoding: utf8|ascii|utf16|binary. For binary, pass base64 in content. overwrite controls whether to replace an existing file.")]
    public OkResult WriteFile(string path, string content, string encoding = "utf8", bool overwrite = false)
    {
        _files.WriteFile(path, content, encoding, overwrite);
        return new OkResult();
    }

    [McpServerTool, Description("Create a folder (and any missing parents).")]
    public OkResult CreateFolder(string path) { _files.CreateFolder(path); return new OkResult(); }

    [McpServerTool, Description("Launch a program. Returns the new process id. UseShellExecute=true so this respects file associations and PATH.")]
    public LaunchAppResult LaunchApp(string path, string? args = null, string? workingDir = null)
        => new(_files.LaunchApp(path, args, workingDir));

    [McpServerTool, Description("Run a PowerShell command and capture stdout/stderr/exit_code. timeout_ms kills the process if exceeded.")]
    public ShellResult Shell(string command, string? workingDir = null, int timeoutMs = 30000)
    {
        var (code, so, se) = _files.Shell(command, workingDir, timeoutMs);
        return new ShellResult(code, so, se);
    }
}
```

- [ ] **Step 5: Register in `Program.cs`**

Add `.AddSingleton<FileService>()` and `.WithTools<FileTools>()`.

- [ ] **Step 6: Manual E2E from Claude Code**

"Create folder C:\\tmp\\mcp-test, write hello.txt with content 'Привет', then read it back." Expected: file created and contents echoed.

- [ ] **Step 7: Commit**

```powershell
git add Mcp.ComputerUse/Core/FileService.cs Mcp.ComputerUse/Tools/FileTools.cs Mcp.ComputerUse/Program.cs Mcp.ComputerUse.Tests/FileServiceTests.cs
git commit -m "feat(tools): read_file/write_file/create_folder/launch_app/shell"
```

---

## Chunk 4: Harden & Optimize

Goal: shrink the binary, surface configuration, structured logging, optional visual flash overlay. Target final binary < 17 MB.

### Task 4.1: Custom ImageSharp Configuration (PNG-only)

**Files:**
- Modify: `Mcp.ComputerUse/Core/ScreenCaptureService.cs`

- [ ] **Step 1: Replace `Configuration.Default` usage with a single-codec Configuration**

Add to the class:

```csharp
private static readonly SixLabors.ImageSharp.Configuration PngOnlyConfig = CreatePngConfig();

private static SixLabors.ImageSharp.Configuration CreatePngConfig()
{
    var cfg = new SixLabors.ImageSharp.Configuration();
    cfg.Configure(new SixLabors.ImageSharp.Formats.Png.PngConfigurationModule());
    return cfg;
}
```

Replace `Image.LoadPixelData<Bgra32>(...)` with `Image.LoadPixelData<Bgra32>(PngOnlyConfig, pixels, w, h)`.

- [ ] **Step 2: AOT publish — measure**

Run: `dotnet publish Mcp.ComputerUse -c Release -r win-x64`
Expected: binary shrinks by ~1-2 MB. Note size in commit message.

- [ ] **Step 3: Re-run capture smoke test**

Run: `dotnet test --filter ScreenCaptureSmokeTests`
Expected: still passes.

- [ ] **Step 4: Commit**

```powershell
git add Mcp.ComputerUse/Core/ScreenCaptureService.cs
git commit -m "perf: use png-only ImageSharp Configuration to shrink AOT binary"
```

### Task 4.2: CLI flags + structured config

**Files:**
- Create: `Mcp.ComputerUse/AppOptions.cs`
- Modify: `Mcp.ComputerUse/Program.cs`
- Modify: `Mcp.ComputerUse/Core/ScreenshotStorage.cs`

- [ ] **Step 1: Write `AppOptions.cs`**

```csharp
namespace Mcp.ComputerUse;

public sealed record AppOptions(
    string? ScreenshotsDir,
    string ScaleTarget,        // "xga" | "wxga" | "fwxga" | "none"
    int? DefaultMonitor,
    bool VisualFlashEnabled,
    Microsoft.Extensions.Logging.LogLevel LogLevel)
{
    public static AppOptions Parse(string[] args)
    {
        string? dir = Environment.GetEnvironmentVariable("MCP_COMPUTERUSE_SCREENSHOTS_DIR");
        string target = "wxga";
        int? defMon = null;
        bool flash = true;
        var lvl = Microsoft.Extensions.Logging.LogLevel.Information;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {a}");
            switch (a)
            {
                case "--screenshots-dir": dir = Next(); break;
                case "--scale-target": target = Next().ToLowerInvariant(); break;
                case "--default-monitor": defMon = int.Parse(Next()); break;
                case "--no-flash": flash = false; break;
                case "--log-level": lvl = Enum.Parse<Microsoft.Extensions.Logging.LogLevel>(Next(), ignoreCase: true); break;
            }
        }

        return new AppOptions(dir, target, defMon, flash, lvl);
    }
}
```

- [ ] **Step 2: Modify `ScreenshotStorage` to accept `AppOptions`**

Replace its constructor:
```csharp
public ScreenshotStorage(AppOptions opts)
{
    DefaultDir = !string.IsNullOrWhiteSpace(opts.ScreenshotsDir)
        ? Path.GetFullPath(opts.ScreenshotsDir!)
        : Environment.CurrentDirectory;
}
```

- [ ] **Step 3: Wire `AppOptions` into `Program.cs`**

At the top, before `Host.CreateApplicationBuilder`:
```csharp
var opts = AppOptions.Parse(args);
```

Register: `.AddSingleton(opts)` ahead of `ScreenshotStorage`.
Change log level: `builder.Logging.SetMinimumLevel(opts.LogLevel);`

- [ ] **Step 4: Build**

Run: `dotnet build -c Release`
Expected: succeeds.

- [ ] **Step 5: Commit**

```powershell
git add Mcp.ComputerUse/AppOptions.cs Mcp.ComputerUse/Program.cs Mcp.ComputerUse/Core/ScreenshotStorage.cs
git commit -m "feat: CLI flags for screenshots-dir, scale-target, default-monitor, log-level"
```

### Task 4.3: Optional visual flash overlay (best-effort)

**Files:**
- Create: `Mcp.ComputerUse/Core/VisualFlash.cs`
- Modify: `Mcp.ComputerUse/Core/InputService.cs`

This is a best-effort debugging aid. If it complicates AOT, drop it (set `flash=false` by default and document).

- [ ] **Step 1: Write `VisualFlash.cs`**

```csharp
namespace Mcp.ComputerUse.Core;

public sealed class VisualFlash
{
    private readonly bool _enabled;
    public VisualFlash(AppOptions opts) => _enabled = opts.VisualFlashEnabled;

    public void At(int sx, int sy)
    {
        if (!_enabled) return;
        // Defer real implementation: a layered window with TransparentColorKey
        // would require additional Win32 APIs (CreateWindowExW, SetLayeredWindowAttributes).
        // v1: log only. v2: actual overlay.
    }
}
```

- [ ] **Step 2: Inject into `InputService` and call `_flash.At(sx, sy)` from `MouseMoveScreen`**

Add `VisualFlash` constructor parameter and `_flash` field. Call inside `MouseMoveScreen`.

- [ ] **Step 3: Register in `Program.cs`**

Add `.AddSingleton<VisualFlash>()`.

- [ ] **Step 4: Build + test**

Run: `dotnet test`
Expected: still passes.

- [ ] **Step 5: Commit**

```powershell
git add Mcp.ComputerUse/Core/VisualFlash.cs Mcp.ComputerUse/Core/InputService.cs Mcp.ComputerUse/Program.cs
git commit -m "feat: visual-flash hook (stub); real overlay deferred to v2"
```

### Task 4.4: Structured logging + error mapping

**Files:**
- Modify each tool class to log entry/exit at Debug level
- Modify: `Mcp.ComputerUse/Program.cs` (logging shape)

- [ ] **Step 1: Inject `ILogger<T>` into each tool class**

Example (apply pattern to all):
```csharp
private readonly ILogger<MonitorTools> _log;
public MonitorTools(MonitorRegistry registry, ILogger<MonitorTools> log)
{
    _registry = registry;
    _log = log;
}
```

In each tool method:
```csharp
_log.LogDebug("tool_call tool={Tool} args={Args}", nameof(ListMonitors), new { });
```

- [ ] **Step 2: Build**

Run: `dotnet build -c Release`
Expected: succeeds.

- [ ] **Step 3: Commit**

```powershell
git add Mcp.ComputerUse/Tools/
git commit -m "obs: ILogger debug entries for every tool call"
```

### Task 4.5: Final AOT publish + smoke

**Files:** none.

- [ ] **Step 1: Clean publish**

Run:
```powershell
Remove-Item -Recurse -Force Mcp.ComputerUse/bin, Mcp.ComputerUse/obj -ErrorAction SilentlyContinue
dotnet publish Mcp.ComputerUse/Mcp.ComputerUse.csproj -c Release -r win-x64
```

- [ ] **Step 2: Measure binary**

```powershell
Get-Item Mcp.ComputerUse/bin/Release/net10.0-windows/win-x64/publish/mcp-computeruse.exe | Select-Object Length
```
Expected: `Length` ≤ ~18,000,000 bytes (≤ 17 MB target). If above, audit IL2026 warnings; consider further codec-trimming, `<DebuggerSupport>false</DebuggerSupport>`, `<EnableUnsafeBinaryFormatterSerialization>false</EnableUnsafeBinaryFormatterSerialization>`.

- [ ] **Step 3: Full E2E in Claude Code**

Script for manual run:
1. "List my monitors." → response includes correct N
2. "Take a screenshot of monitor 0." → image inline in chat + metadata text
3. Reference the screenshot, ask Claude to: "click on the Start button (model coords ~x,y); type 'notepad'; press Enter; wait 1500 ms; take another screenshot."
4. Verify Notepad opens on the correct monitor.

- [ ] **Step 4: Tag release**

```powershell
git tag -a v0.1.0 -m "computer-use mcp v0.1.0: monitors, screenshot, mouse, keyboard, files, launch, shell"
```

---

## Risk register / what to escalate

| Symptom | Likely cause | Action |
|---|---|---|
| Clicks land 2-4 px off on high-DPI laptop | Manifest not honored (mixed-mode init) | Verify `SetProcessDpiAwarenessContext` runs as first line of Main + manifest is embedded |
| `BitBlt` returns black for Chrome / Electron | HW-accelerated DWM composition | Defer to v2; add `Windows.Graphics.Capture` selectively |
| AOT publish > 20 MB | Codec roots / extra deps | Audit IL2026 list; ensure custom PNG-only `Configuration` is wired |
| "JSON value could not be converted" at runtime | Missing `[JsonSerializable]` for a DTO | Add to `McpJsonContext` |
| Access violations after monitor enum | Delegate GC'd | Confirm `GC.KeepAlive(callback)` after `EnumDisplayMonitors` |
| Mouse only works on primary monitor | Missing `MOUSEEVENTF_VIRTUALDESK` | Verify dwFlags in `InputService` |
| MCP host doesn't see tools | Stdout log leak | All loggers must use `LogToStandardErrorThreshold = LogLevel.Trace` |

If any task description proves wrong empirically (e.g., MCP SDK 1.3.0 namespace differs from what's shown), pause and update the plan rather than improvising — the spec is the source of truth and should be updated to reflect what actually compiles.

# Computer-Use MCP Server (.NET 10 AOT) — Design Spec

**Date:** 2026-05-21
**Status:** Approved (design phase)
**Target platform:** Windows 10/11 x64 only
**Distribution:** Single Native-AOT executable (~15-20 MB), spawned over stdio by MCP clients (Claude Code, Claude Desktop)

## 1. Goals

Production-grade Windows computer-use MCP server in C# / .NET 10 with Native AOT, exposing the Anthropic `computer_20250124` action surface plus Windows-MCP coarser tools (multi-monitor screenshot, mouse, keyboard, file I/O, app launch, shell). Vision-first: the model receives a downscaled PNG, returns coordinates in the model's pixel space, and the server remaps them back to physical screen pixels.

## 2. Non-Goals (v1)

UIA / accessibility-tree scraping, DXGI Desktop Duplication, Windows.Graphics.Capture (WinRT), OAuth, sandboxing, video recording, cross-platform support, browser-DOM automation (use Windows-MCP alongside if needed).

## 3. Image Delivery to Claude Code — Core Mechanic

MCP's `tools/call` result returns an array of content blocks. Two block types matter:

```jsonc
{
  "content": [
    { "type": "image", "data": "<base64-PNG>", "mimeType": "image/png" },
    { "type": "text",  "text": "{\"monitor_index\":1,\"orig_w\":3840,\"orig_h\":2160,\"scaled_w\":1280,\"scaled_h\":800,\"factor_x\":0.333,\"factor_y\":0.370,\"saved_to\":\"...\"}" }
  ]
}
```

Claude Code (and any MCP client) **natively** recognises `image` blocks and routes them into the model's vision context — no extra round-trip, no path-then-read. The text block carries the `ScalePlan` so follow-up `mouse_*` calls can use `coord_space="model"` and the server reverses the mapping.

**Hybrid storage** (per user decision):
- PNG is saved to `Environment.CurrentDirectory` (working dir set by MCP client when it spawned the server) by default
- Override order: explicit `save_path` parameter on the tool call → env var `MCP_COMPUTERUSE_SCREENSHOTS_DIR` → CLI flag `--screenshots-dir <path>` → `Environment.CurrentDirectory`
- Filename: `screenshot-mon{index}-{yyyyMMdd-HHmmss-fff}.png`
- Image base64 is ALWAYS included in the response — file save is auxiliary for debugging/history

## 4. Default Image Optimization

Per user decision: **WXGA (1280×800)** is the default downscale target. The model gets ~1365 tokens/image; Anthropic explicitly recommends doing this downscale in the tool rather than relying on their server-side resize. Per-call override via `downscale: bool` (default true) and `grayscale: bool` (default false). Future: a `target` parameter to switch between XGA / WXGA / FWXGA / native.

## 5. Project Structure

```
Mcp.ComputerUse/
├── Mcp.ComputerUse.csproj         # net10.0-windows, RID=win-x64, PublishAot=true
├── app.manifest                    # PerMonitorV2 DPI awareness
├── Program.cs                      # Host builder, DI, stdio transport
├── Native/
│   ├── Win32.cs                    # [LibraryImport] declarations (one partial class)
│   └── NativeTypes.cs              # RECT, MONITORINFOEX, INPUT, MOUSEINPUT, KEYBDINPUT, constants
├── Core/
│   ├── MonitorRegistry.cs          # EnumDisplayMonitors + per-monitor DPI; cached
│   ├── CoordinateMapper.cs         # ScalePlan; Screen↔Model; MAX_SCALING_TARGETS
│   ├── ScreenCaptureService.cs     # BitBlt → GetDIBits → BGRA → ImageSharp PNG (+ resize, grayscale)
│   ├── InputService.cs             # SendInput (mouse/keyboard/scroll/hotkey)
│   └── FileService.cs              # read_file/write_file/create_folder/launch_app/shell
├── Tools/
│   ├── MonitorTools.cs             # list_monitors
│   ├── ScreenTools.cs              # screenshot
│   ├── MouseTools.cs               # mouse_move/click/drag/scroll/cursor_position
│   ├── KeyboardTools.cs            # type_text/key_press/key_hotkey/key_hold/wait
│   └── FileTools.cs                # read_file/write_file/create_folder/launch_app/shell
└── Json/
    └── McpJsonContext.cs           # [JsonSerializable] over every DTO
```

## 6. csproj (final)

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
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ModelContextProtocol" Version="1.3.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="9.*" />
    <PackageReference Include="SixLabors.ImageSharp" Version="4.0.0" />
  </ItemGroup>
</Project>
```

## 7. app.manifest

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

Without PerMonitorV2 on 150%/200%-scaled monitors GetMonitorInfo returns virtualized logical pixels — captures come back blurry-upscaled and mouse coords land off-target.

## 8. Tool Surface (v1, full)

| Tool | Args (snake_case JSON) | Returns |
|---|---|---|
| `list_monitors` | — | `{ monitors: [{ index, device_name, bounds:{x,y,w,h}, work_area, is_primary, dpi_x, dpi_y }] }` |
| `screenshot` | `monitor_index:int`, `downscale:bool=true`, `grayscale:bool=false`, `save_path?:string` | image-block + text-block with `ScreenshotResult` |
| `mouse_move` | `monitor_index`, `x:int`, `y:int`, `coord_space:"model"\|"screen"="model"` | `{ ok:true }` |
| `mouse_click` | `monitor_index`, `x`, `y`, `button:"left"\|"right"\|"middle"="left"`, `clicks:int=1`, `coord_space` | ok |
| `mouse_down` / `mouse_up` | `monitor_index`, `x`, `y`, `button`, `coord_space` | ok |
| `mouse_drag` | `monitor_index`, `from:{x,y}`, `to:{x,y}`, `button="left"`, `coord_space` | ok |
| `mouse_scroll` | `monitor_index`, `x`, `y`, `clicks:int` (±), `direction:"vertical"\|"horizontal"="vertical"`, `coord_space` | ok |
| `cursor_position` | `monitor_index` | `{ x, y }` in model coords for that monitor's last ScalePlan |
| `type_text` | `text:string`, `delay_ms:int=0` | ok (Unicode via `KEYEVENTF_UNICODE`, layout-independent) |
| `key_press` | `key:string` (e.g. `"Enter"`, `"F4"`) | ok |
| `key_hotkey` | `keys:string` (e.g. `"ctrl+shift+esc"`) | ok |
| `key_hold` | `key:string`, `ms:int` | ok |
| `wait` | `ms:int` | ok |
| `read_file` | `path:string`, `encoding:"utf8"\|"binary"="utf8"` | content (text or base64) |
| `write_file` | `path:string`, `content:string`, `encoding`, `overwrite:bool=false` | ok |
| `create_folder` | `path:string` | ok |
| `launch_app` | `path:string`, `args?:string`, `working_dir?:string` | `{ pid }` |
| `shell` | `command:string`, `working_dir?:string`, `timeout_ms:int=30000` | `{ exit_code, stdout, stderr }` (PowerShell) |

## 9. Coordinate Pipeline

```
┌──────────────────────────────────────────────────────────────────┐
│ screenshot(monitor=1)                                            │
│   1. EnumDisplayMonitors → bounds={left=1920, top=0, 3840×2160}  │
│   2. BitBlt over virtual desktop at (1920,0) size 3840×2160      │
│   3. CoordinateMapper.PlanFor(3840,2160,1920,0)                  │
│      → ScalePlan{scaled=1280×800, factor=(0.333,0.370),          │
│                  monitor_left=1920, monitor_top=0}               │
│   4. ImageSharp resize → PNG → base64                            │
│   5. Cache ScalePlan in _planByMonitor[1]                        │
│   6. Return image + text(JSON of ScalePlan)                      │
└──────────────────────────────────────────────────────────────────┘
                              │
                       (model returns x=640,y=400 in scaled space)
                              ▼
┌──────────────────────────────────────────────────────────────────┐
│ mouse_click(monitor=1, x=640, y=400, coord_space=model)          │
│   1. plan = _planByMonitor[1]                                    │
│   2. (sx, sy) = ModelToScreen(plan, 640, 400)                    │
│      = (round(640/0.333)+1920, round(400/0.370)+0)               │
│      = (1920+1923, 0+1081) = (3843, 1081)  ← absolute desktop px │
│   3. Normalize to virtual desktop 0..65535:                      │
│      nx = round((3843 - vsLeft) * 65535 / (vsWidth-1))           │
│      ny = round((1081 - vsTop)  * 65535 / (vsHeight-1))          │
│   4. SendInput INPUT_MOUSE with                                  │
│      dwFlags = MOVE | ABSOLUTE | VIRTUALDESK                     │
│   5. SendInput LEFTDOWN+LEFTUP at same coords                    │
└──────────────────────────────────────────────────────────────────┘
```

`coord_space="screen"` skips ScalePlan and treats x/y as physical desktop pixels (escape hatch for advanced agents).

## 10. AOT-Critical Decisions

1. **Tool registration**: explicit `.WithTools<MonitorTools>().WithTools<ScreenTools>()…` — never `WithToolsFromAssembly()` (IL2026).
2. **JSON**: every request DTO, response DTO, and nested type listed in `McpJsonContext` with `[JsonSerializable]`. SnakeCaseLower naming policy. Context's `.Default.Options` passed to MCP SDK builder.
3. **DPI awareness**: `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` is set via manifest AND defensively called as the very first line of `Main()`.
4. **Logging**: stdout reserved for MCP framing. `builder.Logging.ClearProviders(); builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);`
5. **Delegate liveness**: `EnumDisplayMonitors`-callback is held in a local field for the duration of the call + `GC.KeepAlive(callback)` after.
6. **Marshalling**: `Marshal.SizeOf<T>()` is fine for blittable structs under AOT. Use `[StructLayout(LayoutKind.Sequential)]` everywhere; `INPUT` uses `LayoutKind.Explicit` for the union.
7. **ImageSharp**: emits benign trim warnings (no `IsAotCompatible=true` declared yet) — runs correctly post-3.1.0. Optimization (Stage 4): replace `Configuration.Default` with a custom Configuration that only loads `PngConfigurationModule` to shrink the binary by ~1-2 MB.
8. **Globalization**: `InvariantGlobalization=true` halves the binary; we don't need locale-aware compares.

## 11. Win32 Surface (P/Invoke via LibraryImport)

`user32`: EnumDisplayMonitors, GetMonitorInfoW, SetProcessDpiAwarenessContext, GetSystemMetrics, GetDC, ReleaseDC, SendInput, GetCursorPos, SetCursorPos.
`gdi32`: CreateCompatibleDC, CreateCompatibleBitmap, SelectObject, BitBlt (`SRCCOPY | CAPTUREBLT`), GetDIBits, DeleteObject, DeleteDC.
`shcore`: GetDpiForMonitor.
`kernel32`: GetLastError (implicit through SetLastError=true).

Mouse: `MOUSEEVENTF_MOVE | ABSOLUTE | VIRTUALDESK`. Scroll: `MOUSEEVENTF_WHEEL` with `mouseData = clicks * 120` (WHEEL_DELTA). Typing: `KEYEVENTF_UNICODE` per UTF-16 code unit (surrogate pairs become two INPUTs).

## 12. Error Handling

- Out-of-range coordinates → `ArgumentOutOfRangeException` surfaced as MCP error result (not protocol-level error)
- `monitor_index` not in registry → refresh registry once; if still missing, error
- BitBlt returns 0 → log GetLastError, error result; do NOT auto-fallback to WinRT in v1
- `SendInput` returns 0 → error with GetLastError
- File ops surface IOException messages verbatim (paths can leak; that's expected for local-only server)

## 13. Configuration

| Knob | Source | Default |
|---|---|---|
| Screenshots directory | `--screenshots-dir <path>` CLI > `MCP_COMPUTERUSE_SCREENSHOTS_DIR` env > `Environment.CurrentDirectory` | cwd |
| Default downscale target | `--scale-target xga\|wxga\|fwxga\|none` | wxga |
| Default monitor for screenshots if omitted | `--default-monitor <n>` | 0 (primary) |
| Visual flash overlay on actions | `--no-flash` | enabled (Stage 4 feature) |
| Log level | `--log-level trace\|debug\|info\|warn\|error` (stderr) | info |

## 14. Stage Plan & Acceptance

| Stage | Scope | Acceptance |
|---|---|---|
| 1 (3-4d) | Skeleton, manifest, MonitorRegistry, CoordinateMapper, ScreenCapture, `list_monitors`, `screenshot` | From Claude Code: `list_monitors` returns N monitors; `screenshot(non_primary)` on 150%-DPI display delivers correct 1280×800 PNG into vision context; image visible in chat. AOT publish < 20 MB. |
| 2 (2-3d) | InputService, all `mouse_*`, all keyboard tools, `wait`, `cursor_position` | E2E from Claude Code: "open Notepad on monitor 1, type 'hello', save to C:\tmp\hi.txt" passes. Click off-monitor-0 lands within ±2 px. |
| 3 (1d) | FileService + all file/launch/shell tools | All tools callable; shell timeout works; encoding round-trips. |
| 4 (2-3d) | Optimize binary (custom ImageSharp Configuration), optional flash overlay, CLI flags, structured stderr logs | Binary < 17 MB. Flash visible on click (toggleable). Logs structured (single-line JSON optional). |

## 15. Caveats / Known Risks

- **Sub-XGA monitors**: downscale logic only scales down (matches Anthropic reference). For < 1024 wide, we letterbox to XGA (per README recommendation) rather than upscale. Implement in Stage 1.
- **Opus 4.7 zoom**: `computer_20251124` introduces a `zoom` action and supports up to 2576px long edge with 1:1 coords. We expose `downscale=false` for that path; future `zoom` tool TBD post-v1.
- **DRM/HW-accelerated windows**: BitBlt may return black for some Chrome HW-accel or Electron surfaces. v1 returns the black image with a warning; v2 may add `Windows.Graphics.Capture` fallback selectively.
- **VIRTUALDESK quantization**: 0..65535 across a 7680px-wide desktop = ~0.12 px per unit — fine for vision clicks. Pixel-perfect drags should use `SetCursorPos` + relative `MOUSEEVENTF_MOVE` (future enhancement).
- **MCP SDK version drift**: pin `ModelContextProtocol = 1.3.0`. Review changelog before bumping; csharp-sdk issue #795 is making more APIs accept user-supplied `JsonSerializerOptions`.

## 16. References

- PDF: `docs/Building a Windows Computer-Use MCP Server in C# with Native AOT.pdf`
- anthropics/claude-quickstarts → `computer-use-demo/computer_use_demo/tools/computer.py` (scaling reference)
- CursorTouch/Windows-MCP (Python; tool taxonomy reference)
- modelcontextprotocol/csharp-sdk v1.3.0
- Microsoft Learn: P/Invoke source generation; Multiple Monitor Applications; Per-Monitor V2 DPI

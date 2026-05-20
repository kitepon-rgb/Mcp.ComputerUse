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

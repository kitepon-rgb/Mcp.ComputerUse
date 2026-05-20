namespace Mcp.ComputerUse.Core;

public sealed class ScreenshotStorage
{
    public string DefaultDir { get; }

    public ScreenshotStorage(AppOptions opts)
    {
        DefaultDir = !string.IsNullOrWhiteSpace(opts.ScreenshotsDir)
            ? Path.GetFullPath(opts.ScreenshotsDir!)
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

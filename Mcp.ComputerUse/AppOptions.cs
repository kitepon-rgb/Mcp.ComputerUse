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

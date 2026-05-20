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

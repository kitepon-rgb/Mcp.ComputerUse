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

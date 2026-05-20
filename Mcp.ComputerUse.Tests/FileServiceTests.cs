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

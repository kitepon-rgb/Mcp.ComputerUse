using FluentAssertions;
using Mcp.ComputerUse.Core;
using SixLabors.ImageSharp;
using Xunit;

namespace Mcp.ComputerUse.Tests;

public class ScreenCaptureSmokeTests
{
    [Fact]
    [Trait("Category", "Integration")]
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

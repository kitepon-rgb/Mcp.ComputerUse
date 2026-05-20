using Microsoft.Extensions.Logging;

namespace Mcp.ComputerUse.Core;

/// <summary>
/// Best-effort visual feedback when the cursor is moved by automation.
/// v1: log-only stub. A future v2 may implement a layered transparent overlay
/// window via CreateWindowExW + SetLayeredWindowAttributes.
/// </summary>
public sealed class VisualFlash
{
    private readonly bool _enabled;
    private readonly ILogger<VisualFlash> _log;

    public VisualFlash(AppOptions opts, ILogger<VisualFlash> log)
    {
        _enabled = opts.VisualFlashEnabled;
        _log = log;
    }

    public void At(int sx, int sy)
    {
        if (!_enabled) return;
        _log.LogDebug("visual_flash at screen=({Sx},{Sy})", sx, sy);
        // v2: render a short-lived transparent overlay marker here.
    }
}

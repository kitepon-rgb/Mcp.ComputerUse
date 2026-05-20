using System.Collections.Concurrent;

namespace Mcp.ComputerUse.Core;

public sealed class ScalePlanCache
{
    private readonly ConcurrentDictionary<int, ScalePlan> _byMonitor = new();
    public void Set(int monitorIndex, ScalePlan plan) => _byMonitor[monitorIndex] = plan;
    public ScalePlan? Get(int monitorIndex) => _byMonitor.TryGetValue(monitorIndex, out var p) ? p : null;
}

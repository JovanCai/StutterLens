using StutterLens.Knowledge;
using StutterLens.Platform;

namespace StutterLens.Detection;

public sealed class AntiCheatStatus
{
    public List<string> RunningProcesses { get; } = new();   // 如 "BattlEye (beservice.exe)"
    public List<string> LoadedDrivers { get; } = new();
    public bool Any => RunningProcesses.Count > 0 || LoadedDrivers.Count > 0;
    public IEnumerable<string> Names => RunningProcesses.Concat(LoadedDrivers).Select(s => s.Split(" (")[0]).Distinct();
    public override string ToString() => Any ? string.Join("、", Names) : "未检测到";
}

/// <summary>
/// 反作弊检测：只看“进程名列表”和“已加载驱动名列表”这两类系统公开信息，
/// 不触碰任何反作弊或游戏进程。
/// </summary>
public static class AntiCheatDetector
{
    private static readonly Dictionary<string, string> ProcMap = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> DriverMap = new(StringComparer.OrdinalIgnoreCase);
    private static List<string> _driverCache = new();
    private static double _driverCacheAt = double.MinValue;

    static AntiCheatDetector()
    {
        foreach (var ac in KnowledgeBase.AntiCheats)
        {
            foreach (var p in ac.Processes) ProcMap[p] = ac.Name;
            foreach (var d in ac.Drivers) DriverMap[d] = ac.Name;
        }
    }

    public static AntiCheatStatus Detect(IReadOnlyList<ProcInfo>? snapshot = null, bool refreshDrivers = true)
    {
        var st = new AntiCheatStatus();
        snapshot ??= ProcessTable.Snapshot();
        foreach (var p in snapshot)
            if (ProcMap.TryGetValue(p.Name, out var n))
            {
                var s = $"{n} ({p.Name})";
                if (!st.RunningProcesses.Contains(s)) st.RunningProcesses.Add(s);
            }

        // 驱动列表变化很慢，最多每 5 秒刷新一次
        var now = Core.Clock.NowMs;
        if (refreshDrivers && now - _driverCacheAt > 5000)
        {
            _driverCache = Native.LoadedDrivers().Select(d => d.Name).ToList();
            _driverCacheAt = now;
        }
        foreach (var d in _driverCache)
            if (DriverMap.TryGetValue(d, out var n))
                st.LoadedDrivers.Add($"{n} ({d})");
        return st;
    }
}

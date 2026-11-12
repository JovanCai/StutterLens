using StutterLens.Analysis;
using StutterLens.Collectors;
using StutterLens.Core;
using StutterLens.Knowledge;
using StutterLens.Platform;

namespace StutterLens.Detection;

/// <summary>
/// 统计每个进程的 Present 频率，自动识别“正在玩的游戏”，并把目标游戏的帧时间喂给会话。
/// 识别规则：前台窗口所属进程 + 持续 3 秒以上 ≥15 次/秒 Present + 不在非游戏名单内。
/// </summary>
public sealed class FrameTracker
{
    private sealed class PidStat
    {
        public readonly Dictionary<ulong, int> Counts = new();
        public double LastAny;
    }

    private readonly object _gate = new();
    private readonly Dictionary<int, PidStat> _pids = new();
    private readonly string? _forcedExe;
    private readonly MonitorMode _mode;
    private readonly Func<int> _foreground;
    private int _candidatePid, _candidateSeconds;
    private ulong _primary;
    private double _lastPrimaryT = double.NaN;
    private double _noPresentSince = double.NaN;

    public GameSession? Session { get; private set; }

    /// <summary>游戏不在前台时的卡顿也计入（副屏窗口化游戏时使用）。</summary>
    public bool IncludeBackground { get; set; }

    public event Action<GameSession, Stutter>? StutterDetected;

    public FrameTracker(MonitorMode mode, string? forcedExe, Func<int> foreground)
    {
        _mode = mode;
        _forcedExe = forcedExe;
        _foreground = foreground;
    }

    /// <summary>ETW 线程调用。</summary>
    public void OnPresent(int pid, ulong swapChain, double t)
    {
        Stutter? st = null;
        GameSession? s;
        lock (_gate)
        {
            if (!_pids.TryGetValue(pid, out var ps)) _pids[pid] = ps = new PidStat();
            ps.Counts[swapChain] = ps.Counts.GetValueOrDefault(swapChain) + 1;
            ps.LastAny = t;

            s = Session;
            if (s == null || pid != s.Pid || swapChain != _primary) return;
            if (!double.IsNaN(_lastPrimaryT) && t > _lastPrimaryT)
                st = s.OnFrame(t, t - _lastPrimaryT, () => IncludeBackground || _foreground() == s.Pid);
            _lastPrimaryT = t;
        }
        if (st != null) StutterDetected?.Invoke(s, st);
    }

    /// <summary>
    /// 每秒由采样线程调用。返回 (新开始的会话, 已结束的会话)。
    /// </summary>
    public (GameSession? Started, GameSession? Ended) Tick(List<ProcInfo> snapshot, int fgPid)
    {
        lock (_gate)
        {
            double now = Clock.NowMs;
            var alive = snapshot.ToDictionary(p => p.Pid, p => p);
            GameSession? started = null, ended = null;

            var rates = _pids.ToDictionary(kv => kv.Key, kv => kv.Value.Counts.Values.Sum());

            if (Session != null)
            {
                var s = Session;
                bool processAlive = alive.TryGetValue(s.Pid, out var pi) && pi.Name.Equals(s.Exe, StringComparison.OrdinalIgnoreCase);
                int rate = rates.GetValueOrDefault(s.Pid);

                if (rate > 0) _noPresentSince = double.NaN;
                else if (double.IsNaN(_noPresentSince)) _noPresentSince = now;

                if (!processAlive || (!double.IsNaN(_noPresentSince) && now - _noPresentSince > 60_000))
                {
                    s.EndMs = s.LastFrameMs > 0 ? s.LastFrameMs : now;
                    ended = s;
                    Session = null;
                    _primary = 0;
                    _lastPrimaryT = double.NaN;
                }
                else if (_pids.TryGetValue(s.Pid, out var ps) && ps.Counts.Count > 0)
                {
                    // 主交换链：优先 DXGI/D3D9（真实交换链），无则用内核 PresentHistory
                    var best = ps.Counts.Where(kv => (kv.Key & EtwCollector.KernelSourceFlag) != EtwCollector.KernelSourceFlag && kv.Value >= 5)
                        .OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
                    if (best == 0)
                        best = ps.Counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
                    if (best != 0 && best != _primary && (!ps.Counts.TryGetValue(_primary, out var cur) || cur < ps.Counts[best] / 2))
                    {
                        _primary = best;
                        _lastPrimaryT = double.NaN;
                    }
                }
            }

            if (Session == null)
            {
                int cand = 0;
                if (_forcedExe != null)
                {
                    cand = rates.Where(kv => kv.Value >= 1 && alive.TryGetValue(kv.Key, out var p) && p.Name.Equals(_forcedExe, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
                }
                else if (fgPid > 4 && rates.GetValueOrDefault(fgPid) >= 15 && alive.TryGetValue(fgPid, out var fp)
                         && !KnowledgeBase.NonGameProcesses.Contains(fp.Name) && fgPid != Environment.ProcessId)
                {
                    cand = fgPid;
                }

                if (cand != 0 && cand == _candidatePid) _candidateSeconds++;
                else { _candidatePid = cand; _candidateSeconds = cand != 0 ? 1 : 0; }

                if (cand != 0 && (_candidateSeconds >= 3 || _forcedExe != null))
                {
                    var p = alive[cand];
                    var entry = KnowledgeBase.FindGame(p.Name);
                    Session = new GameSession
                    {
                        Pid = cand,
                        Exe = p.Name,
                        Title = entry?.Title ?? Path.GetFileNameWithoutExtension(p.Name),
                        KnownAntiCheat = entry?.AntiCheat,
                        RequestedMode = _mode,
                        EffectiveMode = _mode,
                        StartMs = now,
                        ImagePath = ProcessTable.ImagePath(cand),
                    };
                    started = Session;
                    _primary = 0;
                    _lastPrimaryT = double.NaN;
                    _noPresentSince = double.NaN;
                    _candidatePid = 0;
                    _candidateSeconds = 0;
                    // 立刻选定主交换链
                    if (_pids.TryGetValue(cand, out var ps) && ps.Counts.Count > 0)
                    {
                        var best = ps.Counts.Where(kv => (kv.Key & EtwCollector.KernelSourceFlag) != EtwCollector.KernelSourceFlag)
                            .OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
                        _primary = best != 0 ? best : ps.Counts.OrderByDescending(kv => kv.Value).First().Key;
                    }
                }
            }

            // 重置计数；清理已退出的进程
            foreach (var pid in _pids.Keys.ToList())
            {
                if (!alive.ContainsKey(pid)) { _pids.Remove(pid); continue; }
                _pids[pid].Counts.Clear();
            }
            return (started, ended);
        }
    }

    /// <summary>当前各进程 Present 频率（调试用）。</summary>
    public Dictionary<int, int> RatesSnapshot()
    {
        lock (_gate) return _pids.ToDictionary(kv => kv.Key, kv => kv.Value.Counts.Values.Sum());
    }

    public GameSession? ForceEnd()
    {
        lock (_gate)
        {
            var s = Session;
            if (s != null) s.EndMs = s.LastFrameMs > 0 ? s.LastFrameMs : Clock.NowMs;
            Session = null;
            return s;
        }
    }
}

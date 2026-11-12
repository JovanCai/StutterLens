using StutterLens.Analysis;
using StutterLens.Collectors;
using StutterLens.Core;
using StutterLens.Detection;
using StutterLens.Platform;
using StutterLens.Report;

namespace StutterLens;

public sealed class MonitorOptions
{
    public MonitorMode Mode = MonitorMode.Auto;
    public string? ForcedExe;
    public string OutDir = ReportWriter.DefaultDir;
    public bool Once;
    public bool IncludeBackground;
    public bool OpenReport = true;
    public double MinSessionSec = 20;
    public int KeepReports;
}

/// <summary>后台监控主循环：采集 → 识别游戏 → 检测卡顿 → 归因 → 出报告。</summary>
public sealed class GameMonitor : IDisposable
{
    private readonly MonitorOptions _opt;
    private readonly SignalStore _store = new();
    private readonly EtwCollector _etw;
    private readonly SystemSampler _sampler;
    private readonly FrameTracker _tracker;
    private readonly CauseAnalyzer _analyzer;
    private readonly object _analyzeGate = new();
    private Timer? _analyzeTimer;
    private double _lastStatus;
    private bool _acWarned;
    private CancellationTokenSource? _cts;

    public List<string> WrittenReports { get; } = new();

    public GameMonitor(MonitorOptions opt)
    {
        _opt = opt;
        _etw = new EtwCollector(_store);
        _sampler = new SystemSampler(_store);
        _tracker = new FrameTracker(opt.Mode, opt.ForcedExe, () => _sampler.ForegroundPid) { IncludeBackground = opt.IncludeBackground };
        _analyzer = new CauseAnalyzer(_store);
    }

    public void Run(CancellationToken token)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        EtwCollector.CleanupStaleSessions();

        var pre = AntiCheatDetector.Detect();
        Log.Info($"检测方式：{ModeName(_opt.Mode)}　|　当前反作弊：{pre}");
        if (pre.Any && _opt.Mode != MonitorMode.Safe)
            Log.Info("检测到反作弊，将使用安全模式：仅读取系统公开数据，不触碰游戏进程。", ConsoleColor.Green);

        _etw.Present += _tracker.OnPresent;
        _etw.StartPresentSession();
        _sampler.Sampled += OnSampled;
        _sampler.Start();
        _analyzeTimer = new Timer(_ => AnalyzePending(false), null, 1000, 500);

        Log.Info(_opt.ForcedExe != null ? $"等待 {_opt.ForcedExe} 开始渲染…" : "后台监控中，启动游戏后自动开始检测。按 Ctrl+C 结束并生成报告。");
        _cts.Token.WaitHandle.WaitOne();

        _analyzeTimer.Dispose();
        _sampler.Sampled -= OnSampled;
        var last = _tracker.ForceEnd();
        if (last != null) FinishSession(last, gameExited: false);
    }

    private void OnSampled(List<ProcInfo> snap, SysSample sample)
    {
        try
        {
            var ac = AntiCheatDetector.Detect(snap);
            var (started, ended) = _tracker.Tick(snap, _sampler.ForegroundPid);

            if (ended != null)
            {
                FinishSession(ended, gameExited: true);
                if (_opt.Once) { _cts?.Cancel(); return; }
            }

            if (started != null)
            {
                _sampler.TargetPid = started.Pid;
                _store.ResetDpcTotals();
                _acWarned = false;
                Log.Info("");
                Log.Info($"▶ 开始检测：{started.Title}（{started.Exe}）", ConsoleColor.Cyan);
                if (started.KnownAntiCheat != null) Log.Info($"  已知反作弊：{started.KnownAntiCheat}");
                if (_sampler.Input.Devices.Count > 0)
                    Log.Info($"  外设：{string.Join("、", _sampler.Input.Devices.Select(d => d.Label))}", ConsoleColor.DarkGray);
            }

            var s = _tracker.Session;
            if (s != null) s.Input.Update(_sampler.Input);
            if (s == null)
            {
                if (_etw.KernelActive) _etw.StopKernelSession();
                _sampler.TargetPid = 0;
                return;
            }

            // ---------------- 反作弊安全控制 ----------------
            // 只要：用户要求安全模式 / 有任何反作弊在运行 / 游戏在已知反作弊名单中，就绝不开启内核追踪。
            foreach (var n in ac.Names) s.AntiCheatsSeen.Add(n);
            bool acPresent = ac.Any || s.KnownAntiCheat != null;
            bool wantKernel = _opt.Mode != MonitorMode.Safe && !acPresent;

            if (!wantKernel)
            {
                if (_etw.KernelActive)
                {
                    _etw.StopKernelSession();
                    Log.Info($"  检测到反作弊（{ac}），已停止内核追踪，切换到安全模式。", ConsoleColor.Green);
                }
                s.EffectiveMode = MonitorMode.Safe;
                if (!_acWarned)
                {
                    _acWarned = true;
                    Log.Info(acPresent
                        ? $"  使用反作弊安全模式，只读取系统公开数据（{(ac.Any ? ac.ToString() : s.KnownAntiCheat)}）"
                        : "  使用安全模式（你指定的）");
                    if (_opt.Mode == MonitorMode.Full && acPresent)
                        Log.Warn("  已指定深度模式，但检测到反作弊。为保护账号，已强制切换到安全模式。");
                }
            }
            // 额外的保守措施：游戏启动 15 秒内都没有发现反作弊，才开启内核追踪
            //（有些反作弊会在游戏窗口出现之后才启动）
            else if (!_etw.KernelActive && s.EffectiveMode != MonitorMode.Safe && Clock.NowMs - s.StartMs > 15_000)
            {
                if (_etw.StartKernelSession())
                {
                    s.KernelUsed = true;
                    s.KernelSinceMs = Clock.NowMs;
                    s.EffectiveMode = MonitorMode.Full;
                    Log.Info("  未检测到反作弊，已启用深度模式（驱动 DPC/ISR 与磁盘 IO 追踪）。");
                }
                else
                {
                    s.EffectiveMode = MonitorMode.Safe;
                    Log.Warn($"  内核追踪启动失败（{_etw.KernelError}），切换到安全模式。");
                }
            }

            if (Clock.NowMs - _lastStatus > 10_000)
            {
                _lastStatus = Clock.NowMs;
                var secs = s.SecondsSnapshot();
                var recent = secs.Skip(Math.Max(0, secs.Count - 6)).Take(5).ToList();
                double fps = recent.Sum(b => b.SumMs) > 0 ? recent.Sum(b => b.Frames) * 1000 / recent.Sum(b => b.SumMs) : 0;
                Log.Info($"  [{DateTime.Now:HH:mm:ss}] {fps,4:F0} FPS | 卡顿 {s.CountedStutters().Count} 次 | GPU {sample.GpuUtil:F0}% CPU {sample.CpuTotal:F0}% | StutterLens 自身 CPU {_sampler.SelfCpuAvg:F2}%", ConsoleColor.DarkGray);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("采样异常（已跳过）：" + ex.Message);
            Log.Debug(ex.ToString());
        }
    }

    private void AnalyzePending(bool all)
    {
        var s = _tracker.Session;
        if (s != null) AnalyzeSession(s, all);
    }

    private void AnalyzeSession(GameSession s, bool all)
    {
        lock (_analyzeGate)
        {
            // 等 2.5 秒再分析：ETW 有约 1 秒的投递延迟，系统采样有 1 秒粒度
            var pending = s.PendingStutters(all ? double.MaxValue : Clock.NowMs - 2500);
            foreach (var st in pending)
            {
                st.Causes = _analyzer.Analyze(st, s, _etw.KernelActive);
                st.Analyzed = true;
                if (st.Background) continue;
                var p = st.Primary!;
                Log.Info($"  ⚠ {Clock.ToLocal(st.T):HH:mm:ss} 停顿 {st.SpanMs:F0}ms → {p.Title}（把握{CauseAnalyzer.Confidence(p.Score)}）",
                    st.Level == "轻微" ? ConsoleColor.Yellow : ConsoleColor.Red);
                Log.Info($"      依据：{p.Evidence}", ConsoleColor.DarkGray);
            }
        }
    }

    private void FinishSession(GameSession s, bool gameExited)
    {
        try
        {
            AnalyzeSession(s, true);
            _etw.StopKernelSession();
            _sampler.TargetPid = 0;
            var played = s.DurationSec >= 60 ? $"{s.DurationSec / 60:F0} 分钟" : $"{s.DurationSec:F0} 秒";
            Log.Info(gameExited ? $"■ {s.Title} 已关闭，这次玩了 {played}。" : $"■ 检测结束，{s.Title} 共检测了 {played}。", ConsoleColor.Cyan);
            if (s.DurationSec < _opt.MinSessionSec || s.Frames < 300)
            {
                Log.Info("  时长过短，不生成报告。");
                return;
            }

            var findings = _analyzer.Summarize(s);
            var cmp = SessionComparer.Compare(s, _opt.OutDir);
            if (cmp != null) findings.Insert(Math.Min(1, findings.Count), cmp);
            var report = new SessionReport
            {
                Session = s,
                Findings = findings,
                ConfigFindings = ConfigChecker.Run(s.ImagePath),
                DpcTop = s.KernelUsed ? _store.DpcTotalsSnapshot() : new(),
                SelfCpuAvg = _sampler.SelfCpuAvg,
                SelfCpuMax = _sampler.SelfCpuMax,
                SelfMemMB = _sampler.SelfWorkingSetMB,
                EventsLost = _etw.PresentEventsLost + _etw.KernelEventsLost,
                Notes = s.EffectiveMode == MonitorMode.Safe && s.KernelUsed
                    ? new List<string> { "会话中途检测到反作弊，内核追踪已被关闭，之后的卡顿只做了安全模式分析。" }
                    : new List<string>(),
            };
            var path = ReportWriter.Write(report, _opt.OutDir);
            WrittenReports.Add(path);
            int pruned = Settings.Prune(_opt.OutDir, _opt.KeepReports);
            if (pruned > 0) Log.Debug($"已删除 {pruned} 局最旧的报告（保留 {_opt.KeepReports} 局）");

            Log.Info($"  平均 {s.AvgFps:F0} FPS，1% Low {s.Low1Fps:F0} FPS。主要结论：");
            foreach (var f in findings.Take(4)) Log.Info($"   · {f.Title}");
            Log.Info($"  报告已保存：{path}", ConsoleColor.Green);
            if (_opt.OpenReport) OpenInBrowser(path);
        }
        catch (Exception ex)
        {
            Log.Warn("报告生成失败：" + ex.Message);
            Log.Debug(ex.ToString());
        }
    }

    /// <summary>
    /// 用默认浏览器打开报告。交给 explorer.exe 打开，浏览器会以普通用户身份运行，
    /// 不会继承本程序的管理员权限。
    /// </summary>
    private static void OpenInBrowser(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\""); }
        catch (Exception ex) { Log.Debug("打开报告失败：" + ex.Message); }
    }

    public static string ModeName(MonitorMode m) => m switch
    {
        MonitorMode.Auto => "自动（检测到反作弊用安全模式，否则用深度模式）",
        MonitorMode.Full => "深度模式（单机游戏）",
        _ => "反作弊安全模式",
    };

    public void Dispose()
    {
        _sampler.Dispose();
        _etw.Dispose();
    }

    // ---------------------------------------------------------------- 调试：探测
    public static void Probe(int seconds, CancellationToken token)
    {
        EtwCollector.CleanupStaleSessions();
        var store = new SignalStore();
        using var etw = new EtwCollector(store);
        var counts = new Dictionary<(int, ulong), int>();
        var gate = new object();
        etw.Present += (pid, sc, t) => { lock (gate) counts[(pid, sc)] = counts.GetValueOrDefault((pid, sc)) + 1; };
        etw.StartPresentSession();
        using var sampler = new SystemSampler(store);
        sampler.Start();
        for (int i = 0; i < seconds && !token.IsCancellationRequested; i++)
        {
            Thread.Sleep(1000);
            Dictionary<(int, ulong), int> c;
            lock (gate) { c = new(counts); counts.Clear(); }
            var names = ProcessTable.Snapshot().ToDictionary(p => p.Pid, p => p.Name);
            var smp = store.Sys.Latest();
            Log.Info($"--- {i + 1}s  前台 PID {sampler.ForegroundPid}  CPU {smp?.CpuTotal:F0}% GPU {smp?.GpuUtil:F0}% VRAM {smp?.VramUsedMB:F0}/{sampler.VramTotalMB:F0}MB 可用内存 {smp?.AvailMB:F0}MB DPC {smp?.DpcPct:F1}%");
            foreach (var kv in c.OrderByDescending(kv => kv.Value).Take(8))
                Log.Info($"   {names.GetValueOrDefault(kv.Key.Item1, "?"),-28} pid={kv.Key.Item1,-6} sc={(kv.Key.Item2 >= EtwCollector.KernelSourceFlag ? "kernel#" + (kv.Key.Item2 & 0xFFFF) : "0x" + kv.Key.Item2.ToString("X"))} presents/s={kv.Value}");
        }
        Log.Info($"ETW 丢失事件：{etw.PresentEventsLost}，自身 CPU {sampler.SelfCpuAvg:F2}%，内存 {sampler.SelfWorkingSetMB}MB");
    }
}

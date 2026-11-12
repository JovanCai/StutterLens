using StutterLens.Collectors;
using StutterLens.Core;
using StutterLens.Knowledge;
using static StutterLens.Core.Tip;

namespace StutterLens.Analysis;

/// <summary>
/// 卡顿归因引擎：对每一次卡顿，在它发生的时间窗口内交叉比对各类系统信号，
/// 给出按可信度排序的“推测原因 + 白话解释 + 分难度的建议”，技术数据单独放在 Tech 里。
/// 这是相关性推断，所以每条结论都附带依据和把握程度。
/// </summary>
public sealed class CauseAnalyzer
{
    private readonly SignalStore _store;
    public CauseAnalyzer(SignalStore store) => _store = store;

    public sealed record CauseInfo(string Title, string Meaning, Tip[] Tips);

    public static readonly Dictionary<string, CauseInfo> Catalog = new()
    {
        ["focus"] = new("窗口切换或弹窗打断",
            "卡顿前后前台窗口发生了切换，切换瞬间游戏画面容易停顿。",
            new[]
            {
                Easy("开启勿扰模式（设置 → 系统 → 通知），屏蔽游戏中的通知弹窗。"),
                Easy("关闭聊天和安全软件的游戏内弹窗。"),
                Easy("尽量使用全屏模式，减少 Alt+Tab。"),
            }),
        ["dpc_unknown"] = new("驱动中断占用偏高（未定位到具体设备）",
            "硬件驱动的中断处理（DPC/ISR）占用 CPU 偏高，会推迟游戏线程。检测到反作弊，为保证账号安全没有开启内核级追踪，所以无法定位到具体设备。",
            new[]
            {
                Easy("更新显卡驱动，这是最常见的来源。"),
                Easy("使用有线网络时，在设备管理器中禁用 Wi-Fi 网卡。"),
                Hands("拔掉暂时不用的 USB 设备，尤其是集线器和灯效控制器。"),
                Pro("用一款没有反作弊的单机游戏，以深度模式（--mode full）复现，可以定位到具体驱动。"),
            }),
        ["disk_io"] = new("硬盘读取阻塞",
            "游戏需要的资源没能及时从硬盘读出，只能等待。机械硬盘和剩余空间不足的硬盘最常见。",
            new[]
            {
                Easy("游戏时避免同时下载、解压或运行杀毒扫描。"),
                Hands("把游戏迁移到 SSD，Steam 等主流平台都支持直接移动安装目录。"),
                Hands("清理游戏所在分区，保留 15% 以上空闲空间。"),
                Pro("用 CrystalDiskInfo 检查硬盘健康状态。"),
            }),
        ["paging"] = new("内存不足，系统在换页",
            "可用内存耗尽后，Windows 会把数据换到硬盘上，游戏用到时再读回来，读写延迟直接变成卡顿。",
            new[]
            {
                Easy("关闭浏览器等占内存的程序（任务管理器里按内存排序查看）。"),
                Hands("确认虚拟内存没有被关闭，建议设为系统自动管理。"),
                Pro("经常出现的话，考虑升级到 16GB 或 32GB 内存。"),
            }),
        ["hard_faults"] = new("游戏在从硬盘调入数据",
            "进入新场景时游戏需要从硬盘加载资源，属于正常行为，但在机械硬盘上会更明显。",
            new[]
            {
                Easy("同一场景再次进入时通常会好转。"),
                Easy("内存占用高的话，关闭后台程序。"),
                Hands("把游戏放在 SSD 上。"),
            }),
        ["mem_low"] = new("可用内存紧张",
            "卡顿时可用内存已经很少，继续增长就会触发换页。",
            new[]
            {
                Easy("关闭浏览器等占内存的程序。"),
                Hands("确认虚拟内存没有被关闭。"),
                Pro("考虑增加内存。"),
            }),
        ["vram_full"] = new("显存已满",
            "显存装不下时，贴图和缓冲要在显存和内存之间来回搬运，造成卡顿。",
            new[]
            {
                Easy("降低纹理质量，这一项对显存影响最大。"),
                Easy("关闭同时占用显卡的程序，比如浏览器视频和动态壁纸。"),
                Hands("仍然不够的话，降低分辨率或关闭光线追踪。"),
            }),
        ["cpu_saturated"] = new("CPU 满载",
            "卡顿时 CPU 整体接近满载，游戏线程拿不到足够的执行时间。",
            new[]
            {
                Easy("关闭后台占用 CPU 的程序，如浏览器、录制和下载软件。"),
                Easy("设置略低于平均帧率的帧数上限，给 CPU 留出余量。"),
                Hands("降低视距、人群密度和物理效果等 CPU 相关画质选项。"),
            }),
        ["cpu_throttle"] = new("CPU 降频",
            "卡顿时 CPU 频率明显低于平时，通常是过热、功耗墙或节能策略导致。",
            new[]
            {
                Easy("笔记本请接通电源并切换到性能模式。"),
                Easy("电源计划切换为“高性能”。"),
                Hands("清理散热器积灰，检查机箱风道。"),
                Pro("温度持续偏高的话，考虑更换硅脂或检查 BIOS 中的功耗限制。"),
            }),
        ["game_internal"] = new("游戏自身卡顿（多为着色器编译）",
            "卡顿时系统层面没有发现异常，问题大概率在游戏内部。最常见的是首次出现某种画面效果时的着色器编译。",
            new[]
            {
                Easy("重复游玩同一场景，编译卡顿会逐渐减少。"),
                Easy("更新游戏。刚更新显卡驱动后着色器会重新编译，前几局卡顿属正常。"),
                Hands("在显卡控制面板中把着色器缓存大小设为 10GB 或无限制。"),
                Pro("游戏支持切换 DX11、DX12 或 Vulkan 的话，可以换一个对比。"),
            }),
        ["periodic"] = new("周期性卡顿",
            "卡顿间隔很规律，基本可以排除游戏本身，常见原因是后台软件定时轮询硬件。",
            new[]
            {
                Easy("逐个退出灯效软件和外设软件（如 Armoury Crate 和 iCUE，以及雷蛇或罗技的驱动软件），每退出一个对比一局。"),
                Easy("关闭 HWiNFO 和 AIDA64 这类硬件监控，或把轮询间隔调到 2 秒以上。"),
                Hands("使用有线网络时禁用 Wi-Fi 网卡，Wi-Fi 后台扫描也会造成周期性卡顿。"),
            }),
        ["early_burst"] = new("开局集中卡顿",
            "卡顿集中在开始几分钟，之后明显减少，典型原因是着色器编译和资源首次加载。",
            new[]
            {
                Easy("进入游戏后先在菜单或安全区停留一两分钟。"),
                Easy("游戏提供着色器预编译的话，等它完成再开始。"),
            }),
        ["gpu_bound"] = new("显卡满载（帧率瓶颈）",
            "显卡持续满载，帧率受显卡性能限制。这会影响整体流畅度，但和突发卡顿不是一回事。",
            new[]
            {
                Easy("降低阴影或关闭光线追踪，这两项最吃显卡。"),
                Easy("开启 DLSS 或 FSR。"),
                Easy("开启 NVIDIA Reflex 或 AMD Anti-Lag，降低输入延迟。"),
            }),
        ["cpu_bound"] = new("CPU 瓶颈",
            "显卡仍有余量而 CPU 已到上限，降低画质对帧率帮助不大。",
            new[]
            {
                Easy("关闭后台程序。"),
                Easy("可以适当提高画质或分辨率，帧率基本不受影响。"),
                Hands("降低视距、人群密度和物理效果。"),
            }),
    };

    public static string Confidence(double score) => score >= 0.65 ? "高" : score >= 0.45 ? "中" : "低";

    /// <summary>中英文混排：英文结尾的名字后面加空格，中文结尾的不加。</summary>
    private static string L(string label) => label.Length > 0 && label[^1] < 0x80 ? label + " " : label;

    public List<CauseHit> Analyze(Stutter s, GameSession sess, bool kernel)
    {
        var hits = new List<CauseHit>();
        double span = Math.Max(s.FrameMs, s.DurationMs);
        double frameStart = s.T - span;
        double a = frameStart - 50, b = s.T + 20;
        double excess = Math.Max(1, span - s.MedianMs);

        // ---------- 1. 前台切换 ----------
        var focus = _store.Focus.Range(frameStart - 1500, s.T + 300);
        if (focus.Count > 0)
            hits.Add(Hit("focus", 0.75, "卡顿前后前台窗口发生过切换", $"前台窗口切换 {focus.Count} 次"));

        // ---------- 2. 深度模式：驱动与硬盘 ----------
        if (kernel)
        {
            // DPC 只有集中在同一个核上才会长时间打断游戏线程。
            // 分散在多核上的大量短 DPC（如显卡垂直同步）是正常现象。
            var groups = _store.Dpc.Range(a, b).GroupBy(d => d.Driver)
                .Select(g => (Driver: g.Key,
                    Total: g.GroupBy(x => x.Cpu).Max(c => c.Sum(x => (double)x.DurMs)),
                    Max: g.Max(x => (double)x.DurMs),
                    Isr: g.Any(x => x.Isr)))
                .OrderByDescending(x => x.Total).Take(2);
            foreach (var g in groups)
            {
                if (g.Max < 0.5 && g.Total < 2.0) continue;
                double score = Math.Clamp(0.2 + g.Total / excess, 0, 0.95);
                if (g.Max >= 2) score = Math.Max(score, 0.7);
                else if (g.Max < 0.5) score = Math.Min(score, 0.45);
                var file = _store.Drivers.Name(g.Driver);
                var dev = KnowledgeBase.DeviceForDriver(file);
                hits.Add(new CauseHit
                {
                    Key = "dpc:" + dev.Key,
                    Title = $"{dev.Device}延迟过高",
                    Meaning = dev.Meaning,
                    Evidence = $"卡顿时{dev.Device}在单个核心上的中断处理耗时明显偏长",
                    Tech = $"{file}：{(g.Isr ? "ISR/" : "")}DPC 单核累计 {g.Total:F1}ms，单次最长 {g.Max:F2}ms",
                    Score = score,
                    Advice = dev.Tips.ToList(),
                });
            }

            var tgtIo = _store.Disk.Range(frameStart - 300, b).Where(d => d.Pid == sess.Pid && !d.Write).ToList();
            if (tgtIo.Count > 0)
            {
                double maxLat = tgtIo.Max(d => d.LatencyMs);
                double mb = tgtIo.Sum(d => (double)d.Bytes) / 1048576;
                if (maxLat >= 30 || mb >= 8)
                {
                    double score = maxLat >= 100 ? 0.8 : maxLat >= 30 ? 0.6 : 0.4;
                    hits.Add(Hit("disk_io", score, $"卡顿时游戏在读盘，最长一次等待 {maxLat:F0}ms",
                        $"游戏读盘 {tgtIo.Count} 次，共 {mb:F1}MB，单次最长 {maxLat:F0}ms"));
                }
            }
        }

        // ---------- 3. 系统采样（两种模式都可用） ----------
        var smp = _store.SampleAt(frameStart + span / 2);
        var history = _store.Sys.Range(s.T - 31_000, s.T - 1_500);
        bool gameCpuSpike = false, gameReadSpike = false;
        if (smp != null)
        {
            if (!kernel && smp.DpcPct + smp.IsrPct >= 5)
                hits.Add(Hit("dpc_unknown", 0.4, "卡顿时驱动中断占用偏高",
                    $"DPC {smp.DpcPct:F1}% + 中断 {smp.IsrPct:F1}%（正常应低于 2%）"));

            bool memLow = (smp.AvailMB > 0 && smp.AvailMB < 1024) || smp.CommitPct >= 90;
            string memTech = $"可用内存 {smp.AvailMB:F0}MB，提交 {smp.CommitPct:F0}%，游戏硬缺页 {smp.TargetHardFaultsPerSec:F0} 次/秒";
            if (smp.TargetHardFaultsPerSec >= 200)
            {
                if (memLow) hits.Add(Hit("paging", 0.8, $"卡顿时可用内存仅 {smp.AvailMB:F0}MB，游戏在频繁从硬盘调页", memTech));
                else hits.Add(Hit("hard_faults", 0.45, "卡顿时游戏在频繁从硬盘调页", memTech));
            }
            else if (memLow)
                hits.Add(Hit("mem_low", 0.4, $"卡顿时可用内存仅 {smp.AvailMB:F0}MB", memTech));

            if (!kernel && smp.DiskLatencyMs >= 50 && smp.DiskBusyPct >= 80)
                hits.Add(Hit("disk_io", 0.45, "卡顿时硬盘满负荷，响应明显变慢",
                    $"磁盘平均响应 {smp.DiskLatencyMs:F0}ms，繁忙 {smp.DiskBusyPct:F0}%"));

            if (smp.VramTotalMB > 0 && smp.VramUsedMB / smp.VramTotalMB >= 0.95)
                hits.Add(Hit("vram_full", 0.55, $"显存占用 {smp.VramUsedMB / 1024:F1}/{smp.VramTotalMB / 1024:F1}GB",
                    $"Dedicated {smp.VramUsedMB:F0}/{smp.VramTotalMB:F0}MB"));

            if (smp.CpuTotal >= 95)
                hits.Add(Hit("cpu_saturated", 0.55, $"卡顿时 CPU 总占用 {smp.CpuTotal:F0}%", $"Processor Utility {smp.CpuTotal:F0}%"));

            var perfBase = Median(history.Select(h => h.CpuPerf).Where(v => v > 0));
            if (perfBase > 0 && smp.CpuPerf > 0 && smp.CpuPerf < perfBase * 0.75)
                hits.Add(Hit("cpu_throttle", 0.5, $"卡顿时 CPU 频率只有平时的 {smp.CpuPerf / perfBase * 100:F0}%",
                    $"% Processor Performance {smp.CpuPerf:F0}（基线 {perfBase:F0}）"));

            // 后台进程突发
            foreach (var p in smp.TopProcs)
            {
                if (p.Pid == sess.Pid || p.Pid <= 4 || p.Pid == Environment.ProcessId
                    || p.Name.Equals(sess.Exe, StringComparison.OrdinalIgnoreCase)) continue; // 同名进程属于游戏自身
                var baseCpu = Median(history.Select(h => h.TopProcs.FirstOrDefault(x => x.Pid == p.Pid)?.CpuPct ?? 0));
                if (p.CpuPct >= 40 && p.CpuPct >= baseCpu * 3 + 10)
                {
                    KnowledgeBase.Culprits.TryGetValue(p.Name, out var cul);
                    // 按“突增量占整机算力的比例”打分，突增越大越可信
                    double share = (p.CpuPct - baseCpu) / (Environment.ProcessorCount * 100.0);
                    double score = 0.35 + Math.Min(0.4, share * 1.5) + (smp.CpuTotal >= 85 ? 0.1 : 0) + (cul != null ? 0.05 : 0);
                    var label = cul?.Label ?? p.Name;
                    hits.Add(new CauseHit
                    {
                        Key = "bg:" + p.Name.ToLowerInvariant(),
                        Title = $"后台程序占用突增：{label}",
                        Meaning = $"卡顿时{L(label)}的 CPU 占用突然升高，挤占了游戏的执行时间。",
                        Evidence = baseCpu < 5
                            ? $"{L(label)}平时几乎不占 CPU，卡顿时占用约 {p.CpuPct / 100:F1} 个核心"
                            : $"{L(label)}卡顿时的 CPU 占用约为平时的 {p.CpuPct / Math.Max(1, baseCpu):F0} 倍",
                        Tech = $"{p.Name}（PID {p.Pid}）{p.CpuPct:F0}%（单核=100%），平时约 {baseCpu:F0}%，整机 CPU {smp.CpuTotal:F0}%",
                        Score = Math.Min(0.9, score),
                        Advice = cul?.Tips.ToList() ?? new List<Tip> { Easy($"确认 {p.Name} 是否需要在游戏时运行，不需要就先关闭。") },
                    });
                }
            }

            var gameBase = Median(history.Select(h => h.TargetCpuPct));
            gameCpuSpike = smp.TargetCpuPct > gameBase + 40;
            gameReadSpike = smp.TargetReadMBps >= 30;
        }

        // ---------- 4. 新进程启动 ----------
        foreach (var ps in _store.ProcStarts.Range(frameStart - 2000, s.T + 1100))
        {
            if (ps.Pid == sess.Pid || ps.ParentPid == sess.Pid || ps.Name.Equals(sess.Exe, StringComparison.OrdinalIgnoreCase)) continue;
            KnowledgeBase.Culprits.TryGetValue(ps.Name, out var cul);
            var label = cul?.Label ?? ps.Name;
            hits.Add(new CauseHit
            {
                Key = "start:" + ps.Name.ToLowerInvariant(),
                Title = $"新进程启动：{label}",
                Meaning = "程序启动时会集中读盘并占用 CPU，可能导致游戏短暂停顿。",
                Evidence = $"{L(label)}在卡顿前后 2 秒内启动",
                Tech = $"{ps.Name}（PID {ps.Pid}，父进程 {ps.ParentPid}）",
                Score = cul != null ? 0.45 : 0.3,
                Advice = cul?.Tips.ToList() ?? new List<Tip>
                {
                    Hands("经常出现的话，在任务管理器的启动应用或任务计划程序中禁用或推迟它。"),
                },
            });
        }

        // ---------- 5. 兜底：游戏内部 ----------
        if (!hits.Any(h => h.Score >= 0.4))
        {
            string ev = gameReadSpike ? "系统层面无异常，游戏此时在大量读盘，较可能在加载新场景"
                : gameCpuSpike ? "系统层面无异常，游戏此时 CPU 占用突增，较可能在编译着色器"
                : "卡顿时驱动和后台程序均无异常，内存与硬盘也正常";
            string tech = smp != null ? $"游戏 CPU {smp.TargetCpuPct:F0}%，读盘 {smp.TargetReadMBps:F0}MB/s" : "";
            hits.Add(Hit("game_internal", gameReadSpike || gameCpuSpike ? 0.5 : 0.35, ev, tech));
        }

        hits.Sort((x, y) => y.Score.CompareTo(x.Score));
        return hits;
    }

    private static CauseHit Hit(string key, double score, string evidence, string tech)
    {
        var c = Catalog[key];
        return new CauseHit { Key = key, Title = c.Title, Meaning = c.Meaning, Evidence = evidence, Tech = tech, Score = score, Advice = c.Tips.ToList() };
    }

    public static string FormatMs(double ms) => ms >= 1000 ? $"{ms / 1000:F1}s" : $"{ms:F0}ms";

    private static double Median(IEnumerable<double> values)
    {
        var arr = values.ToArray();
        if (arr.Length == 0) return 0;
        Array.Sort(arr);
        return arr[arr.Length / 2];
    }

    private static Finding FromCatalog(string key, Severity sev, string detail, string? tech = null)
    {
        var c = Catalog[key];
        return new Finding { Key = key, Severity = sev, Title = c.Title, Detail = c.Meaning + detail, Advice = c.Tips.ToList(), Tech = tech };
    }

    // ================================================================ 会话级总结
    public List<Finding> Summarize(GameSession sess)
    {
        var findings = new List<Finding>();
        var stutters = sess.CountedStutters();
        double minutes = Math.Max(sess.DurationSec / 60, 0.01);

        // 1) 按主要原因聚合
        var groups = stutters.Where(s => s.Primary != null).GroupBy(s => s.Primary!.Key)
            .OrderByDescending(g => g.Count()).ToList();
        foreach (var g in groups)
        {
            var best = g.OrderByDescending(s => s.Primary!.Score).First().Primary!;
            double avgScore = g.Average(s => s.Primary!.Score);
            int count = g.Count();
            int noticeable = g.Count(s => s.Noticeable);
            var techLines = g.Select(s => s.Primary!.Tech).Where(t => !string.IsNullOrEmpty(t)).Distinct().Take(5);
            string share = noticeable > 0
                ? $"本次有 {count} 次卡顿和它有关，其中 {noticeable} 次比较明显"
                : $"本次有 {count} 次轻微顿挫和它有关";
            findings.Add(new Finding
            {
                Key = g.Key,
                // 严重程度看“明显卡顿”，几十毫秒的轻微顿挫再多也不该排在最前面
                Severity = noticeable >= 3 || g.Any(s => s.SpanMs >= 250) ? Severity.Critical
                    : noticeable >= 1 || count >= 10 ? Severity.Warning : Severity.Info,
                Title = best.Title,
                Detail = $"{best.Meaning}{share}，判断把握{Confidence(avgScore)}。依据：{best.Evidence}。",
                Advice = best.Advice,
                Tech = string.Join("\n", techLines),
            });
        }

        // 2) 周期性
        var times = stutters.Select(s => s.T).OrderBy(t => t).ToList();
        if (times.Count >= 6)
        {
            var gaps = times.Zip(times.Skip(1), (x, y) => y - x).Where(d => d > 300).ToList();
            if (gaps.Count >= 5)
            {
                var sorted = gaps.OrderBy(x => x).ToList();
                double period = sorted[sorted.Count / 2];
                int match = gaps.Count(d =>
                {
                    double k = Math.Round(d / period);
                    return k >= 1 && k <= 3 && Math.Abs(d - k * period) <= period * 0.12;
                });
                if (match >= gaps.Count * 0.6)
                    findings.Insert(0, FromCatalog("periodic",
                        stutters.Count(s => s.Noticeable) >= 3 ? Severity.Critical : Severity.Warning,
                        $"本次大约每 {period / 1000:F0} 秒出现一次。",
                        $"{match}/{gaps.Count} 个间隔接近 {period / 1000:F1}s 的整数倍"));
            }
        }

        // 3) 开局集中
        if (stutters.Count >= 6 && sess.DurationSec > 600)
        {
            int early = stutters.Count(s => s.T - sess.StartMs < 180_000);
            double earlyRate = early / 3.0, lateRate = (stutters.Count - early) / Math.Max(1, (sess.DurationSec - 180) / 60);
            if (early >= 4 && earlyRate > lateRate * 4)
                findings.Add(FromCatalog("early_burst", Severity.Info,
                    $"前 3 分钟 {early} 次，之后平均每分钟 {lateRate:F1} 次。"));
        }

        // 4) 帧率瓶颈（这不是卡顿，但玩家常把低帧率也当成卡）
        var sys = _store.Sys.Range(sess.StartMs + 10_000, sess.EndMs > 0 ? sess.EndMs : Clock.NowMs);
        if (sys.Count >= 20)
        {
            double gpu = Median(sys.Select(x => x.GpuTargetUtil > 0 ? x.GpuTargetUtil : x.GpuUtil));
            double core = Median(sys.Select(x => x.CpuMaxCore));
            if (gpu >= 95)
                findings.Add(FromCatalog("gpu_bound", Severity.Info, $"本次显卡占用中位数 {gpu:F0}%。", $"GPU 3D 占用中位数 {gpu:F0}%"));
            else if (gpu < 80 && core >= 90)
                findings.Add(FromCatalog("cpu_bound", Severity.Info, $"本次显卡占用中位数仅 {gpu:F0}%。", $"GPU 中位 {gpu:F0}%，最忙核心中位 {core:F0}%"));
        }

        if (stutters.Count == 0)
        {
            findings.Insert(0, new Finding
            {
                Key = "ok", Severity = Severity.Info, Title = "没有检测到卡顿",
                Detail = $"平均 {sess.AvgFps:F0} FPS，1% Low {sess.Low1Fps:F0} FPS。",
            });
        }
        else
        {
            int big = stutters.Count(s => s.Noticeable);
            int minor = stutters.Count - big;
            double longest = stutters.Max(s => s.SpanMs);
            findings.Insert(0, big == 0
                ? new Finding
                {
                    Key = "overview", Severity = Severity.Info,
                    Title = $"没有明显卡顿，只有 {minor} 次轻微顿挫",
                    Detail = $"最长一次 {longest:F0}ms。这种程度多数人察觉不到，对帧时间敏感的玩家可能会感到偶尔不跟手。下面的分析供参考。",
                }
                : new Finding
                {
                    Key = "overview",
                    Severity = big / minutes >= 1 ? Severity.Critical : Severity.Warning,
                    Title = $"这次有 {big} 次明显卡顿" + (minor > 0 ? $"，另有 {minor} 次轻微顿挫" : ""),
                    Detail = $"明显卡顿指画面停顿超过 50ms，最长一次 {FormatMs(longest)}。下面按影响从大到小列出原因和处理建议。",
                });
        }
        return findings;
    }
}

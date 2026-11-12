using StutterLens.Collectors;
using StutterLens.Core;
using StutterLens.Knowledge;
using StutterLens.Platform;
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
                Easy("纹理质量降一档。这一项对显存影响最大，对画面观感的影响相对小。"),
                Easy("关闭同时占用显卡的程序，比如浏览器视频和动态壁纸。"),
                Hands("关闭光线追踪和帧生成（DLSS 帧生成、FSR 帧生成），两者都要额外占用显存。"),
                Hands("降低分辨率，或开启 DLSS、FSR 等超分辨率，同样能减少显存占用。"),
            }),
        ["gpu_spike"] = new("显卡瞬时满载",
            "卡顿时显卡占用突然冲到满载。通常是画面里出现了特别吃显卡的内容，比如大量粒子特效、体积雾或复杂光照，这几帧来不及渲染完就会停顿。",
            new[]
            {
                Easy("设置一个略低于平均帧率的帧数上限，给显卡留出余量应对突发场景。"),
                Easy("开启 DLSS、FSR 或 XeSS，选“质量”档。"),
                Hands("优先降低这几项：阴影质量、体积雾或体积光、屏幕空间反射、环境光遮蔽。它们对画面影响不大，对帧率影响很大。"),
                Hands("关闭光线追踪，或把光追质量降一档。"),
                Pro("仍然不够的话，把渲染分辨率比例降到 85%～90%。"),
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
        ["bt_link"] = new("蓝牙手柄断开重连",
            "手柄的蓝牙连接中断后又自动连上。游戏通常会在这时重新识别输入设备，画面随之停顿。",
            new[]
            {
                Easy("检查手柄电量，电量低时蓝牙更容易断开。"),
                Easy("缩短手柄和蓝牙适配器的距离，中间不要隔着机箱或显示器。"),
                Easy("改用数据线或手柄自带的接收器连接。"),
                Hands("在设备管理器中打开蓝牙适配器的属性，在“电源管理”里取消勾选“允许计算机关闭此设备以节约电源”。"),
            }),
        ["input_change"] = new("键鼠或手柄断开、接入",
            "卡顿前后有键盘、鼠标或手柄断开、重新连接或新插入。不少游戏在输入设备变化时会重新扫描全部设备，扫描期间画面会停住。",
            new[]
            {
                Easy("游戏中尽量不要插拔 USB 设备。无线键鼠注意电量，电量低时更容易断连。"),
                Easy("无线接收器用延长线或插在前面板，放到离鼠标键盘近一些的地方。"),
                Hands("同一个设备反复断连的话，换一个 USB 接口或换一根线，避开前面板和集线器。"),
                Hands("在设备管理器中打开对应设备（或它所在的 USB 集线器）的属性，在“电源管理”里取消勾选“允许计算机关闭此设备以节约电源”。"),
            }),
        ["bt_gamepad"] = new("蓝牙手柄延迟",
            "手柄通过蓝牙连接。蓝牙信号受到干扰或者适配器性能不足时，部分游戏在读取手柄输入或发送振动时要等蓝牙响应，画面会跟着停住。这种等待发生在游戏内部，系统层面看不到直接证据，需要换一种连接方式对比确认。",
            new[]
            {
                Easy("用数据线或手柄自带的接收器连接，再玩一局。StutterLens 会自动对比两局的卡顿次数。"),
                Easy("在游戏设置中关闭手柄振动，振动指令也要经过蓝牙发送。"),
                Easy("缩短手柄和蓝牙适配器的距离。USB 蓝牙适配器可以接一根延长线放到桌面上，远离 USB 3.0 接口和机箱背板。"),
                Hands("断开暂时不用的蓝牙设备，尤其是蓝牙耳机。"),
                Hands("在设备管理器中打开蓝牙适配器的属性，在“电源管理”里取消勾选“允许计算机关闭此设备以节约电源”。"),
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
            "显卡持续满载，帧率受显卡性能限制。满载时帧时间波动更大，画面不够稳，突发的重场景也更容易卡一下。",
            new[]
            {
                Easy("设置一个略低于平均帧率的帧数上限，让显卡不再长期满载，帧时间会稳定很多。"),
                Easy("开启 DLSS、FSR 或 XeSS，选“质量”档。"),
                Easy("开启 NVIDIA Reflex 或 AMD Anti-Lag，降低满载时的输入延迟。"),
                Hands("优先降低阴影、体积雾、屏幕空间反射和环境光遮蔽；有光线追踪的话先关掉它。"),
                Hands("纹理质量只影响显存，显存够用时不必降。"),
            }),
        ["cpu_bound"] = new("CPU 瓶颈",
            "显卡仍有余量而 CPU 已到上限，降低画质对帧率帮助不大。",
            new[]
            {
                Easy("关闭后台程序。"),
                Easy("可以适当提高画质或分辨率，帧率基本不受影响。"),
                Hands("降低吃 CPU 的选项：视距、人群或 NPC 密度、物理效果、植被密度和阴影距离。"),
                Hands("设置略低于平均帧率的帧数上限，减少 CPU 满载时的帧时间波动。"),
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
            var quietDpc = QuietWindows(sess, s.T, b - a, sess.KernelSinceMs);
            var dpcHist = quietDpc.Count > 0 ? _store.Dpc.Range(quietDpc[^1].From, quietDpc[0].To) : new();
            foreach (var g in groups)
            {
                if (g.Max < 0.5 && g.Total < 2.0) continue;
                double score = Math.Clamp(0.2 + g.Total / excess, 0, 0.95);
                if (g.Max >= 2) score = Math.Max(score, 0.7);
                else if (g.Max < 0.5) score = Math.Min(score, 0.45);
                // 不卡的时候这个驱动也经常这样的话，同时出现只是巧合
                int drv = g.Driver;
                double spec = Specificity(quietDpc, w =>
                {
                    var xs = Slice(dpcHist, w.From, w.To).Where(x => x.Driver == drv).ToList();
                    return xs.Count > 0 && (xs.Max(x => x.DurMs) >= 0.5 || xs.GroupBy(x => x.Cpu).Max(c => c.Sum(x => (double)x.DurMs)) >= 2.0);
                }, out double p0);
                score *= spec;
                var file = _store.Drivers.Name(g.Driver);
                var dev = KnowledgeBase.DeviceForDriver(file);
                hits.Add(new CauseHit
                {
                    Key = "dpc:" + dev.Key,
                    Title = $"{dev.Device}延迟过高",
                    Meaning = dev.Meaning,
                    Evidence = $"卡顿时{dev.Device}在单个核心上的中断处理耗时明显偏长" + CommonNote(spec),
                    Tech = $"{file}：{(g.Isr ? "ISR/" : "")}DPC 单核累计 {g.Total:F1}ms，单次最长 {g.Max:F2}ms" + BaseNote(quietDpc, p0),
                    Score = score,
                    Advice = dev.Tips.ToList(),
                });
            }

            static bool DiskBusy(List<DiskSample> io) =>
                io.Count > 0 && (io.Max(d => d.LatencyMs) >= 30 || io.Sum(d => (double)d.Bytes) >= 8 * 1048576);
            double ioFrom = frameStart - 300;
            var tgtIo = _store.Disk.Range(ioFrom, b).Where(d => d.Pid == sess.Pid && !d.Write).ToList();
            if (DiskBusy(tgtIo))
            {
                double maxLat = tgtIo.Max(d => d.LatencyMs);
                double mb = tgtIo.Sum(d => (double)d.Bytes) / 1048576;
                double score = maxLat >= 100 ? 0.8 : maxLat >= 30 ? 0.6 : 0.4;
                // 机械硬盘上游戏几乎一直在慢慢读盘，要看不卡的时候是不是也这样
                var quietIo = QuietWindows(sess, s.T, b - ioFrom, sess.KernelSinceMs);
                var ioHist = quietIo.Count > 0
                    ? _store.Disk.Range(quietIo[^1].From, quietIo[0].To).Where(d => d.Pid == sess.Pid && !d.Write).ToList()
                    : new();
                double spec = Specificity(quietIo, w => DiskBusy(Slice(ioHist, w.From, w.To)), out double p0);
                hits.Add(Hit("disk_io", score * spec, $"卡顿时游戏在读盘，最长一次等待 {maxLat:F0}ms" + CommonNote(spec),
                    $"游戏读盘 {tgtIo.Count} 次，共 {mb:F1}MB，单次最长 {maxLat:F0}ms" + BaseNote(quietIo, p0)));
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

            // 核显（Intel、骁龙 Adreno 等）只登记很少的“专用显存”，主要用共享内存，
            // 按专用显存算占用率会误报，所以只对 2GB 以上的独立显存做判断
            if (smp.VramTotalMB >= 2048)
            {
                double ratio = smp.VramUsedMB / smp.VramTotalMB;
                // 显存接近上限后，新的资源会溢出到共享内存（走 PCIe 访问，慢很多）
                double spill = smp.VramSharedMB - Median(history.Select(h => h.VramSharedMB));
                bool spilling = ratio >= 0.9 && spill >= 300;
                if (ratio >= 0.95 || spilling)
                    hits.Add(Hit("vram_full", spilling ? 0.65 : 0.55,
                        $"显存占用 {smp.VramUsedMB / 1024:F1}/{smp.VramTotalMB / 1024:F1}GB" + (spilling ? $"，并开始借用内存（比平时多 {spill:F0}MB）" : ""),
                        $"Dedicated {smp.VramUsedMB:F0}/{smp.VramTotalMB:F0}MB，Shared {smp.VramSharedMB:F0}MB（+{Math.Max(0, spill):F0}）"));
            }

            // 显卡瞬时满载：平时有余量，卡顿那一秒冲到满载
            double gpuNow = smp.GpuTargetUtil > 0 ? smp.GpuTargetUtil : smp.GpuUtil;
            double gpuBase = Median(history.Select(h => h.GpuTargetUtil > 0 ? h.GpuTargetUtil : h.GpuUtil));
            if (gpuNow >= 97 && gpuBase > 0 && gpuBase < 88)
                hits.Add(Hit("gpu_spike", 0.5, $"卡顿时显卡占用冲到 {gpuNow:F0}%，平时约 {gpuBase:F0}%",
                    $"GPU 3D {gpuNow:F0}%（基线 {gpuBase:F0}%）"));

            // 其他程序抢占显卡
            foreach (var p in smp.GpuProcs)
            {
                if (p.Pid == sess.Pid || p.Name.Equals(sess.Exe, StringComparison.OrdinalIgnoreCase)) continue;
                var baseG = Median(history.Select(h => h.GpuProcs.FirstOrDefault(x => x.Pid == p.Pid)?.CpuPct ?? 0));
                if (p.CpuPct < 25 || p.CpuPct < baseG * 2 + 15) continue;
                KnowledgeBase.Culprits.TryGetValue(p.Name, out var cul);
                var label = cul?.Label ?? p.Name;
                hits.Add(new CauseHit
                {
                    Key = "bggpu:" + p.Name.ToLowerInvariant(),
                    Title = $"后台程序占用显卡：{label}",
                    Meaning = $"卡顿时{L(label)}突然大量占用显卡，游戏的渲染任务要和它排队。",
                    Evidence = $"{L(label)}卡顿时占用显卡 {p.CpuPct:F0}%，平时约 {baseG:F0}%",
                    Tech = $"{p.Name}（PID {p.Pid}）GPU {p.CpuPct:F0}%，基线 {baseG:F0}%，游戏 GPU {smp.GpuTargetUtil:F0}%",
                    Score = Math.Min(0.8, 0.4 + (p.CpuPct - baseG) / 200),
                    Advice = cul?.Tips.ToList() ?? new List<Tip>
                    {
                        Easy($"游戏时关闭 {p.Name}，或在它的设置里关闭硬件加速。"),
                    },
                });
            }

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

        // ---------- 5. 键鼠手柄断开或接入 ----------
        // 很多游戏在输入设备变化时会重新扫描全部设备，扫描期间画面停住
        var inputEvents = _store.Input.Range(frameStart - 3000, s.T + 1000);
        if (inputEvents.Count > 0)
        {
            var dev = inputEvents[0].Device;
            bool btPad = inputEvents.Any(x => x.Device.Kind == InputKind.Gamepad && x.Device.Transport == Transport.Bluetooth);
            string what = inputEvents.Any(x => !x.Connected) && inputEvents.Any(x => x.Connected) ? "断开后又重新连上"
                : inputEvents.Any(x => !x.Connected) ? "断开了" : "接入了";
            hits.Add(Hit(btPad ? "bt_link" : "input_change", 0.75,
                $"卡顿前后{L(dev.Name)}（{dev.KindName}，{dev.TransportName}）{what}",
                string.Join("，", inputEvents.Select(x => $"{Clock.ToLocal(x.T):HH:mm:ss} {x.Device.Label} {(x.Connected ? "接入" : "断开")}"))));
        }

        // ---------- 6. 兜底：游戏内部；手柄走蓝牙时也可能是蓝牙延迟 ----------
        if (!hits.Any(h => h.Score >= 0.4))
        {
            // 深度模式下能看到游戏有没有加载手柄输入组件，没加载就说明游戏不读手柄
            if (smp?.BtPadConnected == true && (!kernel || _store.GamepadApi(sess.Pid) != null))
                hits.Add(Hit("bt_gamepad", 0.42, "系统层面没有发现其他异常，手柄此时通过蓝牙连接", "蓝牙手柄已连接"));
            string ev = gameReadSpike ? "系统层面无异常，游戏此时在大量读盘，较可能在加载新场景"
                : gameCpuSpike ? "系统层面无异常，游戏此时 CPU 占用突增，较可能在编译着色器"
                : "卡顿时驱动和后台程序均无异常，内存与硬盘也正常";
            string tech = smp != null ? $"游戏 CPU {smp.TargetCpuPct:F0}%，读盘 {smp.TargetReadMBps:F0}MB/s" : "";
            hits.Add(Hit("game_internal", gameReadSpike || gameCpuSpike ? 0.5 : 0.35, ev, tech));
        }

        hits.Sort((x, y) => y.Score.CompareTo(x.Score));
        return hits;
    }

    private static List<string> BluetoothFacts(InputInfo input)
    {
        var facts = new List<string>();
        double pct = input.Seconds > 0 ? input.BtSeconds * 100.0 / input.Seconds : 0;
        var pads = string.Join("、", input.BtPads);
        facts.Add(pct >= 95 ? $"{L(pads)}全程通过蓝牙连接" : $"{L(pads)}约 {pct:F0}% 的时间通过蓝牙连接");
        if (input.BtDrops > 0) facts.Add($"手柄中途断开过 {input.BtDrops} 次");
        var others = input.BtOthers.Select(d => $"{d.Name}（{d.KindName}）").Distinct().ToList();
        if (others.Count > 0) facts.Add($"同时还有蓝牙连接的 {string.Join("、", others)}，和手柄共用蓝牙带宽");
        if (input.Radio != null) facts.Add($"蓝牙适配器是 {input.Radio.Name}，蓝牙 {input.Radio.Version}");
        if (input.Wifi24GHz) facts.Add($"Wi-Fi 连在 2.4GHz 频段（信道 {input.WifiChannel}），和蓝牙在同一频段");
        return facts;
    }

    private static List<Tip> BluetoothTips(InputInfo input, GameSession sess)
    {
        var tips = new List<Tip>();
        if (input.UsbPads.Any())
            tips.Add(Easy("电脑上还插着手柄接收器或有线手柄，可以直接换过去玩一局对比。"));
        if (input.Wifi24GHz)
            tips.Add(Hands("路由器支持的话，让电脑改连 5GHz Wi-Fi，或者改用网线，避免和蓝牙抢同一频段。"));
        // 045E:02E0 是 Xbox One S 手柄的早期固件，走经典蓝牙；更新固件后改用低功耗蓝牙
        if (input.BtPadIds.Any(id => id.Contains("VID_045E&PID_02E0", StringComparison.OrdinalIgnoreCase)))
            tips.Add(Hands("如果是微软原装 Xbox 手柄：它还在用早期固件。在 Microsoft Store 安装“Xbox 配件”应用，用数据线连上手柄更新固件，更新后会改用低功耗蓝牙连接，兼容性更好。"));
        if (input.Radio?.IsOld == true)
            tips.Add(Hands($"蓝牙适配器只支持蓝牙 {input.Radio.Version}" +
                (input.Radio.IsCsrDongle ? "，而且这款 CSR 芯片的适配器仿制品很多，质量参差不齐" : "") +
                "。可以换一个蓝牙 5.0 以上的适配器。"));
        return tips;
    }

    public sealed record Window(double From, double To);

    /// <summary>
    /// 在卡顿之前 60 秒内挑出没有卡顿的同宽时段作为对照组。
    /// 只取 dataSince 之后的时段（内核追踪开启前没有对应数据）。返回的时段从近到远排列。
    /// </summary>
    private static List<Window> QuietWindows(GameSession sess, double t, double width, double dataSince)
    {
        var list = new List<Window>();
        width = Math.Max(width, 50);
        double step = Math.Max(width, 500);
        double stop = Math.Max(t - 61_000, Math.Max(dataSince, sess.StartMs));
        var busy = sess.StutterSpans(t - 62_000, t);
        for (double end = t - 1500; end - width >= stop && list.Count < 60; end -= step)
        {
            double from = end - width;
            if (busy.Any(x => x.End + 300 >= from && x.Start - 300 <= end)) continue;
            list.Add(new Window(from, end));
        }
        return list;
    }

    /// <summary>
    /// 某个现象在不卡的时段里也经常出现的话，它和卡顿同时出现多半是巧合。
    /// 返回分数系数（0.25~1）；对照时段不足 10 个时不做调整。
    /// </summary>
    private static double Specificity(List<Window> quiet, Func<Window, bool> cond, out double p0)
    {
        p0 = double.NaN;
        if (quiet.Count < 10) return 1;
        p0 = quiet.Count(cond) / (double)quiet.Count;
        return Math.Clamp((1 - p0) / 0.6, 0.25, 1);
    }

    private static string CommonNote(double spec) => spec < 0.75 ? "，不过不卡的时候也经常这样，关联不强" : "";

    private static string BaseNote(List<Window> quiet, double p0) =>
        double.IsNaN(p0) ? "" : $"（对照：不卡的 {quiet.Count} 个时段中 {p0 * 100:F0}% 也是如此）";

    private static List<TItem> Slice<TItem>(List<TItem> sorted, double from, double to) where TItem : ITimed
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (sorted[mid].T < from) lo = mid + 1; else hi = mid; }
        var r = new List<TItem>();
        for (int i = lo; i < sorted.Count && sorted[i].T <= to; i++) r.Add(sorted[i]);
        return r;
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
                Advice = best.Advice.ToList(),
                Tech = string.Join("\n", techLines),
            });
        }

        // 1.5) 蓝牙手柄：补充本局的连接环境，没有归到它头上的卡顿也提醒一下
        var input = sess.Input;
        var sessEvents = _store.Input.Range(sess.StartMs, sess.EndMs > 0 ? sess.EndMs : Clock.NowMs).Where(e => !e.Connected).ToList();
        input.Drops = sessEvents.Count;
        input.BtDrops = sessEvents.Count(e => e.Device.Kind == InputKind.Gamepad && e.Device.Transport == Transport.Bluetooth);
        var padApi = _store.GamepadApi(sess.Pid);
        if (input.BtSeconds > 0 && (!sess.KernelUsed || padApi != null))
        {
            var bt = findings.FirstOrDefault(f => f.Key == "bt_gamepad");
            // 没有卡顿直接归到蓝牙、明显卡顿又很少时，不必提
            if (bt == null && stutters.Count(s => s.Noticeable) >= 3)
            {
                var c = Catalog["bt_gamepad"];
                bt = new Finding
                {
                    Key = "bt_gamepad", Title = c.Title, Advice = c.Tips.ToList(),
                    Severity = input.BtDrops > 0 || input.Radio?.IsOld == true || input.Wifi24GHz ? Severity.Warning : Severity.Info,
                    Detail = c.Meaning + "上面归到其他原因的卡顿，也可能有一部分来自蓝牙。",
                };
                findings.Add(bt);
            }
            if (bt != null)
            {
                bt.Detail += "本局情况：" + string.Join("；", BluetoothFacts(input)) + "。";
                var extra = BluetoothTips(input, sess);
                bt.Advice.InsertRange(Math.Min(1, bt.Advice.Count), extra.Where(t => t.Level == "简单"));
                bt.Advice.AddRange(extra.Where(t => t.Level != "简单"));
                bt.Tech = string.Join("\n", new[] { bt.Tech, input.Describe(), padApi != null ? $"游戏加载了 {padApi}" : null }.Where(x => !string.IsNullOrEmpty(x)));
            }
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
                {
                    var pf = FromCatalog("periodic",
                        stutters.Count(s => s.Noticeable) >= 3 ? Severity.Critical : Severity.Warning,
                        $"本次大约每 {period / 1000:F0} 秒出现一次。",
                        $"{match}/{gaps.Count} 个间隔接近 {period / 1000:F1}s 的整数倍");
                    if (input.BtSeconds > 0)
                        pf.Advice.Insert(0, Easy("手柄这局走的是蓝牙，先换数据线或接收器玩一局对比。"));
                    findings.Insert(0, pf);
                }
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

            var vr = sys.Where(x => x.VramTotalMB >= 2048).Select(x => x.VramUsedMB / x.VramTotalMB).ToList();
            if (vr.Count >= 20 && Median(vr) >= 0.9 && !findings.Any(f => f.Key == "vram_full"))
            {
                var c = Catalog["vram_full"];
                findings.Add(new Finding
                {
                    Key = "vram_high", Severity = Severity.Info, Title = "显存长期接近上限",
                    Detail = $"本局大部分时间显存占用在 {Median(vr) * 100:F0}% 以上，暂时没有直接引起卡顿，但进入新场景时容易因为显存不足而停顿。",
                    Advice = c.Tips.ToList(),
                    Tech = $"Dedicated 占用中位数 {Median(vr) * 100:F0}%",
                });
            }
            else if (gpu < 80 && core >= 90)
                findings.Add(FromCatalog("cpu_bound", Severity.Info, $"本次显卡占用中位数仅 {gpu:F0}%。", $"GPU 中位 {gpu:F0}%，最忙核心中位 {core:F0}%"));
        }

        // “设置帧数上限”这类建议给出具体数字：平均帧率的 90% 左右，取整到 5
        if (sess.AvgFps >= 30)
        {
            int cap = (int)(Math.Floor(sess.AvgFps * 0.9 / 5) * 5);
            foreach (var f in findings)
                for (int i = 0; i < f.Advice.Count; i++)
                    if (f.Advice[i].Text.StartsWith("设置一个略低于平均帧率的帧数上限") || f.Advice[i].Text.StartsWith("设置略低于平均帧率的帧数上限"))
                        f.Advice[i] = f.Advice[i] with { Text = f.Advice[i].Text + $"本局平均 {sess.AvgFps:F0} FPS，可以先试 {cap} FPS（游戏内设置或显卡控制面板）。" };
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

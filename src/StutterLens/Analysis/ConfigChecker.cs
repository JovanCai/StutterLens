using System.Management;
using StutterLens.Core;
using StutterLens.Knowledge;
using StutterLens.Platform;
using Microsoft.Win32;
using static StutterLens.Core.Tip;

namespace StutterLens.Analysis;

/// <summary>静态系统配置体检：只读注册表 / WMI / 系统 API，不修改任何设置。</summary>
public static class ConfigChecker
{
    public static List<Finding> Run(string? gamePath = null)
    {
        var list = new List<Finding>();
        void Try(Action a) { try { a(); } catch (Exception ex) { Log.Debug("config check: " + ex.Message); } }

        Try(() => PowerPlan(list));
        Try(() => GameMode(list));
        Try(() => GameDvr(list));
        Try(() => MemoryIntegrity(list));
        Try(() => Memory(list));
        Try(() => PageFile(list));
        Try(() => DiskSpace(list));
        Try(() => RefreshRate(list));
        Try(() => GpuDriverAge(list));
        Try(() => BackgroundApps(list, gamePath == null ? null : Path.GetFileName(gamePath)));
        if (gamePath != null) Try(() => GameDrive(list, gamePath));
        return list;
    }

    private static object? Reg(RegistryKey root, string path, string name)
    {
        using var k = root.OpenSubKey(path);
        return k?.GetValue(name);
    }

    private static readonly Tip HighPerfTip = Easy("在控制面板的电源选项中选择“高性能”，或在 Windows 11 的 设置 → 系统 → 电源 中把电源模式设为“最佳性能”。");

    private static void PowerPlan(List<Finding> list)
    {
        var active = Reg(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", "ActivePowerScheme") as string;
        Native.GetSystemPowerStatus(out var ps);
        if (ps.ACLineStatus == 0)
            list.Add(new Finding
            {
                Key = "battery", Severity = Severity.Critical, Title = "笔记本正在使用电池供电",
                Detail = "电池供电时 CPU 和显卡会被限制功耗，游戏性能明显下降。",
                Advice = { Easy("游戏时接通电源适配器。") },
                Tech = "ACLineStatus = 0",
            });
        if (active == null) return;
        var id = active.ToLowerInvariant();
        if (id == "a1841308-3541-4fab-bc81-f71556f20b4a")
            list.Add(new Finding
            {
                Key = "power_plan", Severity = Severity.Warning, Title = "电源计划为“节能”",
                Detail = "节能计划会限制 CPU 频率，容易引起卡顿。",
                Advice = { HighPerfTip },
                Tech = "ActivePowerScheme = " + active,
            });
        else if (id == "381b4222-f694-41f0-9685-ff5bb260df2e")
            list.Add(new Finding
            {
                Key = "power_plan", Severity = Severity.Info, Title = "电源计划为“平衡”",
                Detail = "多数情况下够用。如果分析中出现 CPU 降频或电源管理相关的原因，可以切换到高性能对比。",
                Advice = { HighPerfTip },
                Tech = "ActivePowerScheme = " + active,
            });
    }

    private static void GameMode(List<Finding> list)
    {
        if (Reg(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled") is int v && v == 0)
            list.Add(new Finding
            {
                Key = "game_mode", Severity = Severity.Info, Title = "Windows 游戏模式未开启",
                Detail = "游戏模式会在游戏运行时抑制 Windows 更新等后台活动。",
                Advice = { Easy("在 设置 → 游戏 → 游戏模式 中开启。") },
            });
    }

    private static void GameDvr(List<Finding> list)
    {
        var dvr = Reg(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled");
        var cap = Reg(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled");
        if (dvr is int d && d == 1 && cap is int c && c == 1)
            list.Add(new Finding
            {
                Key = "game_dvr", Severity = Severity.Warning, Title = "Xbox Game Bar 后台录制已开启",
                Detail = "后台录制会持续占用显卡编码器和磁盘写入。",
                Advice = { Easy("在 设置 → 游戏 → 捕获 中关闭“录制所发生的情况”。") },
            });
    }

    private static void MemoryIntegrity(List<Finding> list)
    {
        if (Reg(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled") is int v && v == 1)
            list.Add(new Finding
            {
                Key = "hvci", Severity = Severity.Info, Title = "内存完整性（HVCI）已开启",
                Detail = "这项安全功能在部分 CPU 上会带来约 5% 的游戏性能损失。",
                Advice =
                {
                    Easy("VALORANT 和 FACEIT 等反作弊可能要求开启此项，玩这类游戏请保持开启。"),
                    Hands("只玩单机游戏的话，可在 Windows 安全中心 → 设备安全性 → 内核隔离 中关闭后对比。"),
                },
                Tech = "HVCI Enabled = 1",
            });
    }

    private static void Memory(List<Finding> list)
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref m)) return;
        double gb = m.ullTotalPhys / 1073741824.0;
        if (gb < 12)
            list.Add(new Finding
            {
                Key = "ram_size", Severity = gb < 7 ? Severity.Critical : Severity.Warning, Title = $"内存容量 {gb:F0}GB，偏小",
                Detail = "主流游戏推荐 16GB 以上，内存不足时容易因换页卡顿。",
                Advice = { Easy("游戏时关闭浏览器等占内存的程序。"), Pro("升级到 16GB 或 32GB 双通道内存。") },
            });
        if (m.dwMemoryLoad >= 80)
            list.Add(new Finding
            {
                Key = "ram_load", Severity = Severity.Warning, Title = $"当前内存占用已达 {m.dwMemoryLoad}%",
                Detail = "启动游戏前内存已接近用满，游戏中容易触发换页。",
                Advice = { Easy("在任务管理器中按内存排序，关闭不需要的程序。") },
            });
    }

    private static void PageFile(List<Finding> list)
    {
        var v = Reg(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "PagingFiles") as string[];
        if (v == null || v.All(string.IsNullOrWhiteSpace))
            list.Add(new Finding
            {
                Key = "pagefile", Severity = Severity.Warning, Title = "虚拟内存（页面文件）已关闭",
                Detail = "关闭页面文件后，内存紧张时游戏容易卡顿甚至崩溃。",
                Advice = { Hands("在 系统属性 → 高级 → 性能设置 → 高级 → 虚拟内存 中勾选“自动管理所有驱动器的分页文件大小”，重启生效。") },
            });
    }

    private static void DiskSpace(List<Finding> list)
    {
        var sysRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            double freePct = d.AvailableFreeSpace * 100.0 / d.TotalSize;
            double freeGb = d.AvailableFreeSpace / 1073741824.0;
            if (freePct < 10 || freeGb < 15)
            {
                bool isSys = d.Name.StartsWith(sysRoot, StringComparison.OrdinalIgnoreCase);
                list.Add(new Finding
                {
                    Key = "disk_space:" + d.Name, Severity = isSys ? Severity.Warning : Severity.Info,
                    Title = $"{d.Name.TrimEnd('\\')} 盘剩余空间不足（{freeGb:F0}GB）",
                    Detail = isSys
                        ? "系统盘空间不足会影响页面文件和着色器缓存。"
                        : "SSD 剩余空间过少时写入性能会下降，影响该盘上游戏的加载。",
                    Advice = { Easy("清理或迁移文件，保留 15% 以上空闲空间。") },
                    Tech = $"{d.Name} 剩余 {freePct:F0}%",
                });
            }
        }
    }

    private static void RefreshRate(List<Finding> list)
    {
        var cur = new Native.DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<Native.DEVMODE>() };
        if (!Native.EnumDisplaySettingsW(null, Native.ENUM_CURRENT_SETTINGS, ref cur)) return;
        int max = cur.dmDisplayFrequency;
        var m = new Native.DEVMODE { dmSize = cur.dmSize };
        for (int i = 0; Native.EnumDisplaySettingsW(null, i, ref m); i++)
            if (m.dmPelsWidth == cur.dmPelsWidth && m.dmPelsHeight == cur.dmPelsHeight) max = Math.Max(max, m.dmDisplayFrequency);
        if (max > cur.dmDisplayFrequency + 5)
            list.Add(new Finding
            {
                Key = "refresh", Severity = Severity.Warning, Title = $"显示器刷新率设为 {cur.dmDisplayFrequency}Hz，最高支持 {max}Hz",
                Detail = "刷新率设低后，帧率再高画面也无法完全显示出来。",
                Advice = { Easy($"在 设置 → 系统 → 显示 → 高级显示 中改为 {max}Hz。") },
            });
    }

    private static void GpuDriverAge(List<Finding> list)
    {
        using var s = new ManagementObjectSearcher("SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController");
        foreach (ManagementObject o in s.Get())
        {
            var name = o["Name"]?.ToString() ?? "";
            if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new Finding
                {
                    Key = "gpu_driver_missing", Severity = Severity.Critical, Title = "显卡驱动未正确安装",
                    Detail = "当前使用的是 Windows 通用显示驱动，显卡性能无法发挥。",
                    Advice = { Easy("从 NVIDIA、AMD 或 Intel 官网下载对应显卡驱动。") },
                    Tech = name,
                });
                continue;
            }
            if (o["DriverDate"] is string ds && ds.Length >= 8 && DateTime.TryParseExact(ds[..8], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date))
            {
                var age = DateTime.Now - date;
                if (age.TotalDays > 365)
                    list.Add(new Finding
                    {
                        Key = "gpu_driver_old", Severity = Severity.Warning, Title = $"显卡驱动已有 {age.TotalDays / 30:F0} 个月未更新",
                        Detail = "新游戏通常针对新驱动优化，旧驱动更容易出现卡顿和兼容问题。",
                        Advice = { Easy("从官网更新到最新的正式版（WHQL）驱动。") },
                        Tech = $"{name}，驱动 {o["DriverVersion"]}，日期 {date:yyyy-MM-dd}",
                    });
            }
        }
    }

    private static readonly HashSet<string> SystemCulprits = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost.exe", "dwm.exe", "audiodg.exe", "msmpeng.exe", "mpdefendercoreservice.exe", "wmiprvse.exe", "searchindexer.exe",
    };

    private static void BackgroundApps(List<Finding> list, string? gameExe)
    {
        var running = ProcessTable.Snapshot().Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => KnowledgeBase.Culprits.ContainsKey(n) && !SystemCulprits.Contains(n)
                        && !n.Equals(gameExe, StringComparison.OrdinalIgnoreCase)).ToList();
        if (running.Count == 0) return;
        var labels = running.Select(n => KnowledgeBase.Culprits[n].Label).Distinct().ToList();
        list.Add(new Finding
        {
            Key = "bg_apps", Severity = Severity.Info, Title = $"有 {labels.Count} 个可能影响游戏的后台程序在运行",
            Detail = "不一定会造成卡顿，供对照排查：" + string.Join("，", labels) + "。",
            Advice = { Easy("分析中出现后台程序或周期性卡顿时，优先退出这些程序后对比。") },
            Tech = string.Join(", ", running),
        });
    }

    private static void GameDrive(List<Finding> list, string gamePath)
    {
        var root = Path.GetPathRoot(gamePath);
        if (string.IsNullOrEmpty(root) || root.Length < 2) return;
        char letter = root[0];
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
        using var part = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{letter}'"));
        uint? disk = null;
        foreach (ManagementObject o in part.Get()) disk = Convert.ToUInt32(o["DiskNumber"]);
        if (disk == null) return;
        using var pd = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT MediaType, BusType, FriendlyName FROM MSFT_PhysicalDisk WHERE DeviceId = '{disk}'"));
        foreach (ManagementObject o in pd.Get())
        {
            int media = Convert.ToInt32(o["MediaType"] ?? 0);
            if (media == 3)
                list.Add(new Finding
                {
                    Key = "game_on_hdd", Severity = Severity.Critical, Title = $"游戏安装在机械硬盘上（{letter} 盘）",
                    Detail = "现代游戏大量依赖流式加载，机械硬盘是加载卡顿和贴图延迟的常见原因。",
                    Advice = { Hands("把游戏迁移到 SSD，Steam 等主流平台都支持直接移动安装目录，无需重新下载。") },
                    Tech = $"{o["FriendlyName"]}，MediaType = HDD",
                });
        }
    }
}

using StutterLens.Core;
using static StutterLens.Core.Tip;

namespace StutterLens.Knowledge;

public sealed record AntiCheatSignature(string Name, string[] Processes, string[] Drivers);

public sealed record GameEntry(string Exe, string Title, string? AntiCheat);

/// <summary>后台程序：Label 是给普通玩家看的名字。</summary>
public sealed record CulpritInfo(string Label, Tip[] Tips);

/// <summary>一类硬件（驱动按它归类，报告里只说“显卡”“网卡”，不说文件名）。</summary>
public sealed record DeviceInfo(string Key, string Device, string Meaning, Tip[] Tips);

/// <summary>静态知识库：反作弊签名、已知游戏、常见后台程序与硬件驱动的白话建议。</summary>
public static class KnowledgeBase
{
    // ---------------- 反作弊 ----------------
    // 进程名与驱动文件名均为小写。名单宁可多不可少，多了只会更保守地切到安全模式。
    public static readonly AntiCheatSignature[] AntiCheats =
    {
        new("Easy Anti-Cheat", new[] { "easyanticheat.exe", "easyanticheat_eos.exe" }, new[] { "easyanticheat.sys", "easyanticheat_eos.sys", "easyanticheat_eossys.sys" }),
        new("BattlEye", new[] { "beservice.exe", "beservice_x64.exe" }, new[] { "bedaisy.sys" }),
        new("Riot Vanguard", new[] { "vgc.exe", "vgtray.exe" }, new[] { "vgk.sys" }),
        new("腾讯 ACE / TP", new[] { "sguard64.exe", "sguardsvc64.exe", "ace-tray.exe", "acesvc.exe" }, new[] { "ace-base.sys", "ace-game.sys", "ace-core.sys", "tessafe.sys", "tp3helper.sys" }),
        new("nProtect GameGuard", new[] { "gamemon.des", "gamemon64.des", "gameguard.des" }, Array.Empty<string>()),
        new("XIGNCODE3", new[] { "xxd-0.xem" }, new[] { "xhunter1.sys" }),
        new("FACEIT", new[] { "faceit.exe", "faceitservice.exe" }, new[] { "faceit.sys" }),
        new("ESEA", new[] { "esea.exe", "eseaclient.exe" }, new[] { "eseadriver2.sys", "eseadriver3.sys" }),
        new("EA Javelin", new[] { "eaanticheat.gameservice.exe", "eaanticheat.gameservicelauncher.exe" }, new[] { "eaanticheat.sys" }),
        new("PunkBuster", new[] { "pnkbstra.exe", "pnkbstrb.exe" }, Array.Empty<string>()),
        new("miHoYo 反作弊", Array.Empty<string>(), new[] { "mhyprot.sys", "mhyprot2.sys", "mhyprot3.sys", "hoyokprotect.sys" }),
        new("网易反作弊", new[] { "neac_service.exe" }, new[] { "neacsafe64.sys" }),
        new("Denuvo Anti-Cheat", Array.Empty<string>(), new[] { "denuvo-anti-cheat.sys" }),
        new("Wellbia / 其他", Array.Empty<string>(), new[] { "xigncode.sys" }),
    };

    // ---------------- 已知游戏 ----------------
    public static readonly GameEntry[] Games =
    {
        new("cs2.exe", "Counter-Strike 2", "VAC"),
        new("dota2.exe", "Dota 2", "VAC"),
        new("valorant-win64-shipping.exe", "VALORANT", "Riot Vanguard"),
        new("league of legends.exe", "英雄联盟", "Riot Vanguard"),
        new("r5apex.exe", "Apex Legends", "Easy Anti-Cheat"),
        new("r5apex_dx12.exe", "Apex Legends", "Easy Anti-Cheat"),
        new("fortniteclient-win64-shipping.exe", "Fortnite", "Easy Anti-Cheat / BattlEye"),
        new("tslgame.exe", "PUBG", "BattlEye"),
        new("rainbowsix.exe", "彩虹六号：围攻", "BattlEye"),
        new("rainbowsix_be.exe", "彩虹六号：围攻", "BattlEye"),
        new("destiny2.exe", "Destiny 2", "BattlEye"),
        new("escapefromtarkov.exe", "Escape from Tarkov", "BattlEye"),
        new("gta5.exe", "GTA V", "BattlEye"),
        new("gta5_enhanced.exe", "GTA V Enhanced", "BattlEye"),
        new("rustclient.exe", "Rust", "Easy Anti-Cheat"),
        new("deadbydaylight-win64-shipping.exe", "Dead by Daylight", "Easy Anti-Cheat"),
        new("overwatch.exe", "守望先锋", "Blizzard 反作弊"),
        new("cod.exe", "Call of Duty", "Ricochet"),
        new("bf2042.exe", "Battlefield 2042", "EA Javelin"),
        new("bf6.exe", "Battlefield 6", "EA Javelin"),
        new("yuanshen.exe", "原神", "miHoYo 反作弊"),
        new("genshinimpact.exe", "Genshin Impact", "miHoYo 反作弊"),
        new("starrail.exe", "崩坏：星穹铁道", "miHoYo 反作弊"),
        new("zenlesszonezero.exe", "绝区零", "miHoYo 反作弊"),
        new("deltaforceclient-win64-shipping.exe", "三角洲行动", "腾讯 ACE"),
        new("narakabladepoint.exe", "永劫无间", "网易反作弊 / EAC"),
        new("marvel-win64-shipping.exe", "漫威争锋", "网易反作弊"),
        new("crossfire.exe", "穿越火线", "腾讯 ACE"),
        new("dnf.exe", "地下城与勇士", "腾讯 ACE"),
        new("helldivers2.exe", "Helldivers 2", "nProtect GameGuard"),
        new("lostark.exe", "Lost Ark", "Easy Anti-Cheat"),
        new("thefinals.exe", "THE FINALS", "Easy Anti-Cheat"),
        new("eldenring.exe", "艾尔登法环", "Easy Anti-Cheat"),
        new("wallpaper32.exe", "Wallpaper Engine", null),
        new("wallpaper64.exe", "Wallpaper Engine", null),
    };

    // ---------------- 不是游戏的常见渲染进程（自动识别时排除） ----------------
    public static readonly HashSet<string> NonGameProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm.exe", "explorer.exe", "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "msedgewebview2.exe",
        "discord.exe", "steamwebhelper.exe", "steam.exe", "epicgameslauncher.exe", "eadesktop.exe", "battle.net.exe",
        "wegame.exe", "ubisoftconnect.exe", "obs64.exe", "obs32.exe", "searchhost.exe", "startmenuexperiencehost.exe",
        "shellexperiencehost.exe", "textinputhost.exe", "applicationframehost.exe", "widgets.exe", "code.exe",
        "wechat.exe", "weixin.exe", "qq.exe", "nvidia overlay.exe", "nvidia app.exe", "nvcontainer.exe", "radeonsoftware.exe",
        "taskmgr.exe", "lockapp.exe", "windowsterminal.exe", "conhost.exe", "gamebar.exe", "gamebarftserver.exe",
        "rtss.exe", "msiafterburner.exe", "spotify.exe", "cloudmusic.exe", "potplayermini64.exe", "vlc.exe",
        "stutterlens.exe", "claude.exe", "devenv.exe", "rider64.exe", "idea64.exe", "wallpaper64.exe", "wallpaper32.exe",
        "snippingtool.exe", "screenclippinghost.exe", "systemsettings.exe", "photos.exe", "mspaint.exe", "notepad.exe",
        "feishu.exe", "lark.exe", "dingtalk.exe", "telegram.exe", "slack.exe", "teams.exe", "ms-teams.exe", "zoom.exe",
    };

    // ---------------- 后台程序 ----------------
    private static readonly Tip[] DefenderTips =
    {
        Easy("把游戏目录加入 Defender 排除项（Windows 安全中心 → 病毒和威胁防护 → 管理设置 → 排除项）。"),
        Easy("避免在游戏时运行全盘扫描。"),
    };
    private static readonly Tip[] UpdateTips =
    {
        Easy("游戏前先装完系统更新并重启。"),
        Easy("或在 Windows 更新里暂停更新，把“活跃时间”设为常玩游戏的时段。"),
    };
    private static readonly Tip[] IndexTips =
    {
        Hands("在“索引选项”里把游戏目录移出索引范围。"),
    };
    private static readonly Tip[] BrowserTips =
    {
        Easy("游戏时关闭浏览器，至少关掉在播放视频或直播的标签页。"),
    };
    private static readonly Tip[] RgbTips =
    {
        Easy("退出该软件后再玩一局，确认卡顿是否消失。"),
        Hands("确认是它的话，关闭灯效同步或卸载软件，只保留驱动。"),
    };
    private static readonly Tip[] MonitorTips =
    {
        Easy("游戏时关闭它，对比卡顿是否消失。"),
        Hands("需要常驻的话，把传感器轮询间隔调到 2000ms 以上。"),
    };
    private static readonly Tip[] SecurityTips =
    {
        Easy("开启该软件的游戏模式，或把游戏目录加入信任区。"),
    };

    public static readonly Dictionary<string, CulpritInfo> Culprits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msmpeng.exe"] = new("Windows Defender 实时扫描", DefenderTips),
        ["mpdefendercoreservice.exe"] = new("Windows Defender", DefenderTips),
        ["tiworker.exe"] = new("Windows 更新（安装中）", UpdateTips),
        ["trustedinstaller.exe"] = new("Windows 更新（安装中）", UpdateTips),
        ["mousocoreworker.exe"] = new("Windows 更新（检查中）", UpdateTips),
        ["wuauclt.exe"] = new("Windows 更新", UpdateTips),
        ["searchindexer.exe"] = new("Windows 搜索索引", IndexTips),
        ["searchprotocolhost.exe"] = new("Windows 搜索索引", IndexTips),
        ["compattelrunner.exe"] = new("微软兼容性遥测任务", new[]
        {
            Easy("系统计划任务，偶发可以忽略。"),
            Pro("频繁出现的话，在任务计划程序中禁用 Microsoft Compatibility Appraiser。"),
        }),
        ["onedrive.exe"] = new("OneDrive 同步", new[] { Easy("游戏时暂停 OneDrive 同步。") }),
        ["steam.exe"] = new("Steam 客户端", new[]
        {
            Easy("暂停 Steam 下载。"),
            Hands("在 Steam 设置 → 下载 → 着色器预缓存 中关闭“允许在后台处理 Vulkan 着色器”。"),
        }),
        ["fossilize_replay.exe"] = new("Steam 着色器预编译", new[]
        {
            Easy("等预编译完成再玩，之后游戏内的编译卡顿会明显减少。"),
        }),
        ["chrome.exe"] = new("Chrome 浏览器", BrowserTips),
        ["msedge.exe"] = new("Edge 浏览器", new[]
        {
            BrowserTips[0],
            Hands("在 Edge 设置 → 系统和性能 中关闭“启动增强”和关闭后继续运行后台应用。"),
        }),
        ["firefox.exe"] = new("Firefox 浏览器", BrowserTips),
        ["discord.exe"] = new("Discord", new[] { Hands("关闭 Discord 的游戏内覆盖和硬件加速。") }),
        ["obs64.exe"] = new("OBS 录制/直播", new[]
        {
            Hands("编码器换成硬件编码（NVENC、AMF 或 QSV）。"),
            Hands("降低录制分辨率或帧率。"),
        }),
        ["wallpaper64.exe"] = new("Wallpaper Engine", new[] { Easy("在 Wallpaper Engine 设置 → 性能 中，把“其他应用程序全屏时”设为“暂停”。") }),
        ["wallpaper32.exe"] = new("Wallpaper Engine", new[] { Easy("在 Wallpaper Engine 设置 → 性能 中，把“其他应用程序全屏时”设为“暂停”。") }),
        ["nvidia share.exe"] = new("NVIDIA 叠加层/录制", new[] { Easy("在 NVIDIA App 中关闭即时重放和游戏内叠加层。") }),
        ["nvcontainer.exe"] = new("NVIDIA 后台服务", new[] { Easy("在 NVIDIA App 中关闭即时重放和性能监控叠加层。") }),
        ["audiodg.exe"] = new("Windows 音频引擎", new[]
        {
            Hands("关闭播放设备的音频增强。"),
            Hands("更新声卡驱动。"),
        }),
        ["icue.exe"] = new("海盗船 iCUE", RgbTips),
        ["lghub.exe"] = new("罗技 G HUB", RgbTips),
        ["lghub_agent.exe"] = new("罗技 G HUB", RgbTips),
        ["armourycrate.service.exe"] = new("华硕 Armoury Crate", RgbTips),
        ["lightingservice.exe"] = new("华硕灯效服务", RgbTips),
        ["razer synapse service.exe"] = new("雷蛇 Synapse", RgbTips),
        ["signalrgb.exe"] = new("SignalRGB", RgbTips),
        ["openrgb.exe"] = new("OpenRGB", RgbTips),
        ["hwinfo64.exe"] = new("HWiNFO 硬件监控", MonitorTips),
        ["aida64.exe"] = new("AIDA64 硬件监控", MonitorTips),
        ["360tray.exe"] = new("360 安全卫士", SecurityTips),
        ["360sd.exe"] = new("360 杀毒", SecurityTips),
        ["qqpcrtp.exe"] = new("腾讯电脑管家", SecurityTips),
        ["hipsdaemon.exe"] = new("火绒安全", SecurityTips),
        ["svchost.exe"] = new("Windows 系统服务", new[] { Easy("多为系统维护任务，偶发可以忽略。") }),
        ["dwm.exe"] = new("桌面窗口管理器", new[]
        {
            Easy("尽量使用全屏模式。"),
            Hands("窗口化游戏时，在 设置 → 系统 → 显示 → 图形 中开启“窗口化游戏优化”。"),
        }),
        ["wmiprvse.exe"] = new("WMI 系统查询", new[] { Easy("通常由硬件监控或管理类软件频繁查询引起，关闭这类软件后对比。") }),
    };

    // ---------------- 硬件驱动（DPC/ISR 归因） ----------------
    private const string DriverMeaning = "这个驱动处理硬件中断（DPC/ISR）耗时过长，占住了 CPU 核心，游戏线程只能排队等待。";

    public static readonly DeviceInfo[] Devices =
    {
        new("gpu", "显卡驱动", DriverMeaning, new[]
        {
            Easy("从官网下载最新驱动，安装时选择清洁安装。"),
            Easy("关闭显卡软件的游戏内叠加层和即时重放。"),
            Hands("多台显示器刷新率不同时，拔掉副屏对比一局。"),
            Pro("问题仍在的话，用 DDU 在安全模式下彻底卸载后重装驱动。"),
            Pro("切换“硬件加速 GPU 计划”（设置 → 系统 → 显示 → 图形），重启后对比。"),
        }),
        new("net", "网卡驱动", DriverMeaning, new[]
        {
            Easy("使用有线网络时，在设备管理器中禁用 Wi-Fi 网卡。"),
            Easy("开着加速器或 VPN 的话，先关掉对比。"),
            Hands("从主板或笔记本官网更新网卡驱动。"),
            Hands("在网卡属性 → 高级 中关闭各项节能选项。"),
        }),
        new("disk", "存储驱动", DriverMeaning, new[]
        {
            Hands("更新芯片组驱动和 SSD 固件。"),
            Pro("用 CrystalDiskInfo 检查硬盘健康状态。"),
        }),
        new("power", "电源管理（ACPI/CPU 节能）", DriverMeaning, new[]
        {
            Easy("电源计划切换为“高性能”，笔记本请接通电源并使用性能模式。"),
            Pro("更新 BIOS 和芯片组驱动。"),
        }),
        new("usb", "USB 控制器", DriverMeaning, new[]
        {
            Easy("拔掉暂时不用的 USB 设备，尤其是集线器和灯效控制器。"),
            Easy("鼠标回报率在 4000Hz 以上的话，降到 1000Hz 对比。"),
            Hands("鼠标键盘直接接主板背板接口，不经过集线器或前面板。"),
        }),
        new("bluetooth", "蓝牙驱动", DriverMeaning, new[]
        {
            Easy("手柄改用数据线或接收器连接，对比一局。"),
            Easy("断开暂时不用的蓝牙设备，尤其是蓝牙耳机。"),
            Hands("从主板或笔记本官网更新蓝牙驱动。"),
            Hands("在设备管理器中打开蓝牙适配器的属性，在“电源管理”里取消勾选“允许计算机关闭此设备以节约电源”。"),
        }),
        new("audio", "声卡驱动", DriverMeaning, new[]
        {
            Hands("关闭播放设备的音频增强和空间音效。"),
            Hands("从主板或笔记本官网更新声卡驱动。"),
            Easy("不用显示器喇叭的话，在设备管理器中禁用显卡的 HDMI 音频设备。"),
        }),
        new("system", "Windows 内核", "这部分是系统计时和调度本身的开销，通常不是问题根源。", new[]
        {
            Easy("优先排查报告里的其他原因。"),
        }),
        new("other", "其他设备驱动", DriverMeaning, new[]
        {
            Hands("回想最近新装的外设或驱动，先移除后对比。"),
            Pro("根据技术细节里的驱动文件名查找对应设备，更新或禁用后对比。"),
        }),
    };

    // 驱动文件名前缀 → 硬件类别
    private static readonly (string Prefix, string Device)[] DriverPrefixes =
    {
        ("nvlddmkm", "gpu"), ("amdkmdag", "gpu"), ("atikmdag", "gpu"), ("igdkmd", "gpu"), ("dxgkrnl", "gpu"), ("dxgmms", "gpu"),
        ("qcdx", "gpu"),   // 高通 Adreno（Windows on ARM）
        ("nvhda", "audio"), ("hdaudbus", "audio"), ("portcls", "audio"), ("rtkvhd", "audio"),
        ("ndis", "net"), ("tcpip", "net"), ("netio", "net"), ("rt640", "net"), ("rt68", "net"), ("rtux", "net"), ("rtwlan", "net"),
        ("netwtw", "net"), ("netadaptercx", "net"), ("e1d", "net"), ("e2f", "net"), ("killer", "net"),
        ("storport", "disk"), ("stornvme", "disk"), ("storahci", "disk"), ("iastor", "disk"),
        ("acpi", "power"), ("intelppm", "power"), ("amdppm", "power"),
        ("usbxhci", "usb"), ("usbport", "usb"), ("ucx01000", "usb"), ("usbhub", "usb"), ("hidclass", "usb"),
        // KMDF 驱动的 DPC 都记在 Wdf01000 名下，游戏时最常见的来源是 USB 控制器（USB 蓝牙适配器也挂在它下面）
        ("wdf01000", "usb"),
        ("bthport", "bluetooth"), ("bthusb", "bluetooth"), ("bthenum", "bluetooth"), ("bthmini", "bluetooth"),
        ("hidbth", "bluetooth"), ("bthle", "bluetooth"), ("ibtusb", "bluetooth"), ("btfilter", "bluetooth"),
        ("rtkbtfilter", "bluetooth"), ("mtkbtfilter", "bluetooth"), ("bcbtums", "bluetooth"),
        ("ntoskrnl", "system"), ("hal", "system"),
    };

    public static DeviceInfo DeviceForDriver(string driverFile)
    {
        var lower = driverFile.ToLowerInvariant();
        foreach (var (prefix, dev) in DriverPrefixes)
            if (lower.StartsWith(prefix)) return Devices.First(d => d.Key == dev);
        return Devices.First(d => d.Key == "other");
    }

    public static GameEntry? FindGame(string exe) =>
        Games.FirstOrDefault(g => g.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));
}

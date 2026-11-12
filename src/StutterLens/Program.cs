using System.Diagnostics;
using System.Text;
using StutterLens;
using StutterLens.Analysis;
using StutterLens.Collectors;
using StutterLens.Core;
using StutterLens.Detection;
using StutterLens.Platform;

Console.OutputEncoding = Encoding.UTF8;

var argList = args.ToList();
string cmd = argList.Count > 0 && !argList[0].StartsWith("-") ? argList[0].ToLowerInvariant()
    : argList.Contains("--version") ? "version"
    : argList.Contains("--help") || argList.Contains("-h") ? "help"
    : "monitor";
string? Opt(string name)
{
    int i = argList.IndexOf(name);
    return i >= 0 && i + 1 < argList.Count ? argList[i + 1] : null;
}
bool Flag(string name) => argList.Contains(name);

if (Flag("--verbose")) Log.Verbose = true;
if (Opt("--log") is { } logPath) Log.OpenFile(logPath);

switch (cmd)
{
    case "help":
    case "-h":
    case "--help":
        PrintHelp();
        return 0;

    case "version":
    case "--version":
        Console.WriteLine($"StutterLens {AppVersion()}");
        return 0;

    case "check":
        Log.Info("StutterLens 系统设置检查（只读，不修改任何设置）\n");
        Log.Info($"正在运行的反作弊：{AntiCheatDetector.Detect()}\n");
        var findings = ConfigChecker.Run(Opt("--game"));
        if (findings.Count == 0) Log.Info("未发现明显的设置问题。", ConsoleColor.Green);
        foreach (var f in findings.OrderByDescending(f => f.Severity))
        {
            var color = f.Severity switch { Severity.Critical => ConsoleColor.Red, Severity.Warning => ConsoleColor.Yellow, _ => ConsoleColor.Cyan };
            Log.Info($"[{(f.Severity == Severity.Critical ? "优先处理" : f.Severity == Severity.Warning ? "建议处理" : "供参考")}] {f.Title}", color);
            if (f.Detail.Length > 0) Log.Info("    " + f.Detail);
            foreach (var a in f.Advice) Log.Info($"    → [{a.Level}] {a.Text}", ConsoleColor.DarkGray);
        }
        return 0;

    case "anticheat":
        var st = AntiCheatDetector.Detect();
        Log.Info($"运行中的反作弊进程：{(st.RunningProcesses.Count == 0 ? "无" : string.Join("、", st.RunningProcesses))}");
        Log.Info($"已加载的反作弊驱动：{(st.LoadedDrivers.Count == 0 ? "无（没用管理员身份运行时可能查不到）" : string.Join("、", st.LoadedDrivers))}");
        return 0;

    case "monitor":
    case "probe":
        break;

    default:
        Log.Warn($"未知命令：{cmd}");
        PrintHelp();
        return 1;
}

// ---------------- 需要 ETW 的命令：确保管理员权限 ----------------
if (!EtwCollector.IsElevated)
{
    if (Flag("--no-elevate"))
    {
        Log.Warn("读取 ETW 帧数据需要管理员权限。");
        return 2;
    }
    Log.Info("读取 ETW 帧数据需要管理员权限，正在请求提权…");
    try
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
            Verb = "runas",
            UseShellExecute = true,
        });
        return 0;
    }
    catch
    {
        Log.Warn("未获得管理员权限，已退出。");
        PauseIfOwnConsole();
        return 2;
    }
}

using var cts = new CancellationTokenSource();
using var finished = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
// 用户直接点窗口的关闭按钮、注销或关机时，Windows 会给约 5 秒，用来收尾并写出报告
Native.ConsoleCtrlHandler closeHandler = type =>
{
    if (type is 2 or 5 or 6)
    {
        cts.Cancel();
        finished.Wait(4500);
        return true;
    }
    return false;
};
Native.SetConsoleCtrlHandler(closeHandler, true);
if (Opt("--duration") is { } dur && double.TryParse(dur, out var secs)) cts.CancelAfter(TimeSpan.FromSeconds(secs));

if (cmd == "probe")
{
    GameMonitor.Probe(int.TryParse(Opt("--seconds"), out var n) ? n : 10, cts.Token);
    return 0;
}

var opt = new MonitorOptions
{
    Mode = (Opt("--mode") ?? "auto").ToLowerInvariant() switch
    {
        "full" or "deep" => MonitorMode.Full,
        "safe" => MonitorMode.Safe,
        _ => MonitorMode.Auto,
    },
    ForcedExe = Opt("--process") is { } p ? (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p : p + ".exe") : null,
    OutDir = Opt("--out") ?? StutterLens.Report.ReportWriter.DefaultDir,
    Once = Flag("--once"),
    IncludeBackground = Flag("--include-background"),
    OpenReport = !Flag("--no-open"),
};
if (double.TryParse(Opt("--min-session"), out var ms)) opt.MinSessionSec = ms;
Log.Quiet = Flag("--quiet");

Log.Info($"StutterLens {AppVersion()} · 游戏卡顿检测与诊断");
int exitCode = 0;
int reports = 0;
try
{
    using var mon = new GameMonitor(opt);
    mon.Run(cts.Token);
    reports = mon.WrittenReports.Count;
}
catch (Exception ex)
{
    Log.Warn("运行出错：" + ex.Message);
    Log.Debug(ex.ToString());
    exitCode = 1;
}
finally
{
    finished.Set();
}
GC.KeepAlive(closeHandler);
// 双击运行且没有生成报告时，留住窗口让用户看到提示
if (!cts.IsCancellationRequested || reports == 0 || exitCode != 0) PauseIfOwnConsole();
return exitCode;

static void PauseIfOwnConsole()
{
    if (Log.Quiet || Console.IsInputRedirected || !Native.OwnsConsole()) return;
    Console.WriteLine();
    Console.WriteLine("按回车键关闭窗口…");
    try { Console.ReadLine(); } catch { }
}

static string AppVersion() =>
    typeof(GameMonitor).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

static void PrintHelp()
{
    Console.WriteLine(@"StutterLens - 游戏卡顿检测与诊断

用法：
  StutterLens [monitor] [选项]   后台监控，自动识别游戏，游戏结束时生成报告（默认）
  StutterLens check              系统配置体检（无需管理员）
  StutterLens anticheat          列出检测到的反作弊组件
  StutterLens probe [--seconds N] 调试：列出各进程的帧提交频率

monitor 选项：
  --mode auto|full|safe   auto：有反作弊→安全模式，否则深度模式（默认）
                          full：深度模式（单机游戏，可定位到具体驱动和磁盘），检测到反作弊时仍会强制切换到安全模式
                          safe：反作弊安全模式（只用系统公开计数器与图形事件）
  --process name.exe      只监控指定游戏
  --out 目录              报告输出目录（默认 %LOCALAPPDATA%\StutterLens\Reports）
  --once                  第一个游戏会话结束后退出
  --include-background    游戏不在前台时的卡顿也计入（副屏窗口化游戏）
  --no-open               生成报告后不自动打开浏览器
  --duration 秒           运行指定时长后自动结束
  --log 文件              同时写日志文件
  --quiet                 不输出控制台信息
");
}

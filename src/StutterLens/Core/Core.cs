namespace StutterLens.Core;

/// <summary>统一时间基准：所有信号都换算成“自程序启动以来的毫秒数”。</summary>
public static class Clock
{
    public static readonly DateTime Origin = DateTime.UtcNow;
    public static double NowMs => (DateTime.UtcNow - Origin).TotalMilliseconds;
    public static double ToMs(DateTime utc) => (utc - Origin).TotalMilliseconds;
    public static DateTime ToLocal(double ms) => Origin.AddMilliseconds(ms).ToLocalTime();
}

public interface ITimed { double T { get; } }

/// <summary>简单日志：控制台 + 可选文件。</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;
    public static bool Verbose { get; set; }
    public static bool Quiet { get; set; }

    public static void OpenFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _file = new StreamWriter(path, append: true, System.Text.Encoding.UTF8) { AutoFlush = true };
    }

    public static void Info(string msg, ConsoleColor? color = null) => Write(msg, color, false);
    public static void Debug(string msg) { if (Verbose) Write("[debug] " + msg, ConsoleColor.DarkGray, false); }
    public static void Warn(string msg) => Write(msg, ConsoleColor.Yellow, false);

    private static void Write(string msg, ConsoleColor? color, bool _)
    {
        lock (Gate)
        {
            _file?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}");
            if (Quiet) return;
            if (color.HasValue) Console.ForegroundColor = color.Value;
            Console.WriteLine(msg);
            if (color.HasValue) Console.ResetColor();
        }
    }
}

/// <summary>定长环形缓冲区，按时间范围查询。写入方单线程、读取方加锁。</summary>
public sealed class TimedRing<T> where T : ITimed
{
    private readonly T[] _buf;
    private int _head, _count;
    private readonly object _gate = new();

    public TimedRing(int capacity) => _buf = new T[capacity];

    public void Add(T item)
    {
        lock (_gate)
        {
            _buf[_head] = item;
            _head = (_head + 1) % _buf.Length;
            if (_count < _buf.Length) _count++;
        }
    }

    /// <summary>返回 [from, to] 时间范围内的元素（按时间升序）。允许少量乱序。</summary>
    public List<T> Range(double from, double to)
    {
        var r = new List<T>();
        lock (_gate)
        {
            int outOfRange = 0;
            for (int i = 0; i < _count; i++)
            {
                int idx = (_head - 1 - i + _buf.Length) % _buf.Length;
                var it = _buf[idx];
                if (it.T < from)
                {
                    // 允许少量乱序事件，连续 64 个越界后才停止
                    if (++outOfRange > 64) break;
                    continue;
                }
                outOfRange = 0;
                if (it.T <= to) r.Add(it);
            }
        }
        r.Sort((a, b) => a.T.CompareTo(b.T));
        return r;
    }

    public T? Latest()
    {
        lock (_gate)
        {
            if (_count == 0) return default;
            return _buf[(_head - 1 + _buf.Length) % _buf.Length];
        }
    }

    public void Clear() { lock (_gate) { _head = 0; _count = 0; } }
}

/// <summary>监控模式。</summary>
public enum MonitorMode
{
    /// <summary>自动：检测到反作弊则用安全模式，否则用深度模式。</summary>
    Auto,
    /// <summary>深度模式（单机游戏）：额外开启内核 ETW（DPC/ISR、磁盘 IO）以精确定位驱动与磁盘问题。</summary>
    Full,
    /// <summary>反作弊安全模式：只使用系统级公开计数器与图形 Present 事件，不启用任何内核追踪。</summary>
    Safe,
}

public enum Severity { Info, Warning, Critical }

/// <summary>一条可操作的建议。Level：简单 / 中等 / 进阶。</summary>
public sealed record Tip(string Level, string Text)
{
    public static Tip Easy(string t) => new("简单", t);
    public static Tip Hands(string t) => new("中等", t);
    public static Tip Pro(string t) => new("进阶", t);
}

/// <summary>一条诊断结论（系统配置检查或会话分析结果）。</summary>
public sealed class Finding
{
    public string Key { get; init; } = "";
    public Severity Severity { get; init; }
    public string Title { get; init; } = "";
    /// <summary>用大白话解释“这是什么意思”。</summary>
    public string Detail { get; set; } = "";
    public List<Tip> Advice { get; init; } = new();
    /// <summary>给懂电脑的人看的技术细节（报告里默认折叠）。</summary>
    public string? Tech { get; set; }
}

using StutterLens.Core;

namespace StutterLens.Analysis;

public sealed class CauseHit
{
    public string Key { get; init; } = "";      // 聚合键，如 dpc:gpu、bg:msmpeng.exe
    public string Title { get; init; } = "";    // 白话标题
    public string Meaning { get; init; } = "";  // 这是什么意思
    public string Evidence { get; init; } = ""; // 白话依据
    public string Tech { get; init; } = "";     // 技术细节（驱动文件名、原始数值）
    public double Score { get; init; }          // 0~1 推测可信度
    public List<Tip> Advice { get; init; } = new();
}

public sealed class Stutter
{
    public double T;              // 卡顿帧结束（Present）时刻
    public double FrameMs;        // 最差一帧耗时
    public double DurationMs;     // 连续坏帧的总时长
    public double MedianMs;       // 卡顿前的中位帧时间
    public bool Background;       // 卡顿时游戏不在前台（不计入统计）
    public bool Analyzed;
    public List<CauseHit> Causes = new();

    /// <summary>画面实际停住的时长（连续坏帧合并后）。</summary>
    public double SpanMs => Math.Max(FrameMs, DurationMs);
    /// <summary>超过 50ms 的停顿多数人能明显察觉。</summary>
    public bool Noticeable => SpanMs >= 50;
    public string Level => SpanMs >= 250 ? "严重" : SpanMs >= 50 ? "明显" : "轻微";
    public CauseHit? Primary => Causes.Count > 0 ? Causes[0] : null;
}

public sealed class SecondBucket
{
    public int Sec;
    public int Frames;
    public double SumMs;
    public double MaxMs;
}

/// <summary>一次“游戏会话”（从识别到游戏开始渲染，到游戏退出）。</summary>
public sealed class GameSession
{
    public int Pid { get; init; }
    public string Exe { get; init; } = "";
    public string? ImagePath { get; set; }
    public string Title { get; set; } = "";
    public string? KnownAntiCheat { get; set; }
    public MonitorMode RequestedMode { get; init; }
    public MonitorMode EffectiveMode { get; set; }
    public HashSet<string> AntiCheatsSeen { get; } = new();
    public bool KernelUsed { get; set; }
    public double StartMs { get; init; }
    public double EndMs { get; set; }
    public double LastFrameMs { get; private set; }

    private const double WarmupMs = 10_000;   // 前 10 秒通常是加载，不计卡顿
    private const int Window = 120;           // 基线窗口：最多 120 帧
    private const double WindowMs = 3_000;    // 且只看最近 3 秒，让基线能跟上“持续掉帧”
    private const int MinWindowFrames = 10;

    private readonly object _gate = new();
    private readonly int[] _hist = new int[20_001];    // 0.1ms 一格，覆盖 0~2000ms
    private long _frames;
    private double _sumMs;
    private readonly float[] _win = new float[Window];
    private readonly double[] _winT = new double[Window];
    private readonly float[] _scratch = new float[Window];
    private int _wpos, _wcount;
    private bool _prevBad;            // 上一帧是否已超阈值（连续坏帧合并为一次卡顿）
    public List<SecondBucket> Seconds { get; } = new();
    public List<Stutter> Stutters { get; } = new();

    public long Frames { get { lock (_gate) return _frames; } }

    /// <summary>喂入一帧；若判定为卡顿则返回新的 Stutter（合并后的会返回 null）。</summary>
    public Stutter? OnFrame(double t, double ft, Func<bool> isForeground)
    {
        if (ft <= 0) return null;
        if (ft > 3000) { LastFrameMs = t; return null; } // 超过 3 秒视为暂停/最小化/加载，不计入

        lock (_gate)
        {
            LastFrameMs = t;
            _frames++;
            _sumMs += ft;
            _hist[Math.Min(_hist.Length - 1, (int)(ft * 10))]++;

            int sec = (int)((t - StartMs) / 1000);
            if (sec >= 0)
            {
                var last = Seconds.Count > 0 ? Seconds[^1] : null;
                if (last == null || last.Sec != sec)
                {
                    last = new SecondBucket { Sec = sec };
                    Seconds.Add(last);
                }
                last.Frames++;
                last.SumMs += ft;
                last.MaxMs = Math.Max(last.MaxMs, ft);
            }

            Stutter? result = null;
            bool bad = false;
            // 基线 = 最近 3 秒内（最多 120 帧）帧时间的中位数
            int n = 0;
            double frameStart = t - ft;
            for (int i = 0; i < _wcount; i++)
            {
                int idx = (_wpos - 1 - i + Window) % Window;
                if (frameStart - _winT[idx] > WindowMs) break;
                _scratch[n++] = _win[idx];
            }
            if (n >= MinWindowFrames && t - StartMs > WarmupMs)
            {
                Array.Sort(_scratch, 0, n);
                double median = _scratch[n / 2];
                double threshold = Math.Max(2.0 * median, median + 12.0);
                if (ft > threshold && ft >= 20)
                {
                    bad = true;
                    var prev = Stutters.Count > 0 ? Stutters[^1] : null;
                    if (prev != null && !prev.Analyzed && (_prevBad || t - prev.T < 150))
                    {
                        // 连续的坏帧合并为一次卡顿（一次“冻结”只算一次）
                        prev.FrameMs = Math.Max(prev.FrameMs, ft);
                        prev.DurationMs += ft;
                        prev.T = t;
                    }
                    else if (!_prevBad)
                    {
                        result = new Stutter { T = t, FrameMs = ft, DurationMs = ft, MedianMs = median, Background = !isForeground() };
                        Stutters.Add(result);
                    }
                }
            }
            _prevBad = bad;

            _win[_wpos] = (float)ft;
            _winT[_wpos] = t;
            _wpos = (_wpos + 1) % Window;
            if (_wcount < Window) _wcount++;
            return result;
        }
    }

    public double DurationSec => ((EndMs > 0 ? EndMs : LastFrameMs) - StartMs) / 1000;

    public double AvgFps { get { lock (_gate) return _sumMs > 0 ? _frames * 1000 / _sumMs : 0; } }

    /// <summary>帧时间分位数（ms）。</summary>
    public double FrameTimePercentile(double p)
    {
        lock (_gate)
        {
            if (_frames == 0) return 0;
            long target = (long)Math.Ceiling(_frames * p);
            long acc = 0;
            for (int i = 0; i < _hist.Length; i++)
            {
                acc += _hist[i];
                if (acc >= target) return (i + 0.5) / 10.0;
            }
            return 2000;
        }
    }

    public double Low1Fps => FrameTimePercentile(0.99) is var f and > 0 ? 1000 / f : 0;
    public double Low01Fps => FrameTimePercentile(0.999) is var f and > 0 ? 1000 / f : 0;

    public List<Stutter> CountedStutters()
    {
        lock (_gate) return Stutters.Where(s => !s.Background && s.Analyzed).ToList();
    }

    public List<SecondBucket> SecondsSnapshot()
    {
        lock (_gate) return Seconds.Select(b => new SecondBucket { Sec = b.Sec, Frames = b.Frames, SumMs = b.SumMs, MaxMs = b.MaxMs }).ToList();
    }

    public List<Stutter> PendingStutters(double olderThan)
    {
        lock (_gate) return Stutters.Where(s => !s.Analyzed && s.T < olderThan).ToList();
    }
}

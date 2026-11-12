using StutterLens.Analysis;
using StutterLens.Core;

namespace StutterLens.Tests;

public class StutterDetectionTests
{
    private static GameSession NewSession() => new() { Pid = 1, Exe = "game.exe", Title = "Game", StartMs = 0 };

    /// <summary>以固定帧时间喂入 count 帧，返回结束时刻。</summary>
    private static double Feed(GameSession s, double t, double ft, int count, List<Stutter> found)
    {
        for (int i = 0; i < count; i++)
        {
            t += ft;
            var st = s.OnFrame(t, ft, () => true);
            if (st != null) found.Add(st);
        }
        return t;
    }

    [Fact]
    public void SteadyFrames_ProduceNoStutter()
    {
        var s = NewSession();
        var found = new List<Stutter>();
        Feed(s, 0, 16.7, 60 * 30, found);
        Assert.Empty(found);
        Assert.InRange(s.AvgFps, 59, 61);
    }

    [Fact]
    public void SingleSpike_AfterWarmup_IsDetectedAsNoticeable()
    {
        var s = NewSession();
        var found = new List<Stutter>();
        double t = Feed(s, 0, 16.7, 60 * 15, found);   // 15 秒，越过 10 秒热身期
        t += 120;
        var st = s.OnFrame(t, 120, () => true);
        Assert.NotNull(st);
        Assert.True(st!.Noticeable);
        Assert.Equal("明显", st.Level);
        Feed(s, t, 16.7, 120, found);
        Assert.Empty(found);
    }

    [Fact]
    public void SpikeDuringWarmup_IsIgnored()
    {
        var s = NewSession();
        var found = new List<Stutter>();
        double t = Feed(s, 0, 16.7, 60 * 3, found);
        t += 200;
        Assert.Null(s.OnFrame(t, 200, () => true));
    }

    [Fact]
    public void ConsecutiveBadFrames_MergeIntoOneStutter()
    {
        var s = NewSession();
        var found = new List<Stutter>();
        double t = Feed(s, 0, 16.7, 60 * 15, found);
        Feed(s, t, 60, 3, found);                       // 连续 3 个 60ms 坏帧
        Assert.Single(found);
        Assert.True(found[0].SpanMs >= 180 - 0.1);
        Assert.Single(s.Stutters);
    }

    [Fact]
    public void SustainedFrameRateDrop_IsNotReportedAsManyStutters()
    {
        var s = NewSession();
        var found = new List<Stutter>();
        double t = Feed(s, 0, 8.3, 120 * 15, found);   // 120 FPS
        Feed(s, t, 50, 20 * 30, found);                 // 掉到 20 FPS 并持续 30 秒
        Assert.True(found.Count <= 1, $"持续掉帧被报告了 {found.Count} 次卡顿");
    }

    [Fact]
    public void BackgroundStutter_IsNotCounted()
    {
        var s = NewSession();
        var found = new List<Stutter>();
        double t = Feed(s, 0, 16.7, 60 * 15, found);
        t += 150;
        var st = s.OnFrame(t, 150, () => false);
        Assert.NotNull(st);
        Assert.True(st!.Background);
        st.Analyzed = true;
        Assert.Empty(s.CountedStutters());
    }

    [Fact]
    public void Percentiles_ReflectSlowFrames()
    {
        var s = NewSession();
        var found = new List<Stutter>();
        double t = Feed(s, 0, 10, 980, found);
        Feed(s, t, 40, 20, found);   // 最慢的 2% 是 40ms，第 99 百分位落在其中
        Assert.InRange(s.FrameTimePercentile(0.5), 9.5, 10.5);
        Assert.InRange(s.Low1Fps, 20, 30);
    }
}

using System.Text.Json;
using StutterLens.Core;
using static StutterLens.Core.Tip;

namespace StutterLens.Analysis;

/// <summary>
/// 和同一个游戏之前的报告对比。手柄连接方式这类无法从系统层面直接观察的因素，
/// 靠“换一种方式再玩一局”的前后对比最有说服力。
/// </summary>
public static class SessionComparer
{
    public sealed record PastSession(DateTime Start, double DurationSec, double NoticeablePerMin, double AvgFps, string Connection);

    private const double MinSec = 120;

    public static Finding? Compare(GameSession s, string reportDir)
    {
        try
        {
            if (!Directory.Exists(reportDir)) return null;
            var past = Directory.EnumerateFiles(reportDir, "*.json")
                .OrderByDescending(f => f).Take(200)
                .Select(f => Load(f, s.Exe)).Where(p => p != null).Select(p => p!).ToList();
            var cur = Current(s);
            return Compare(cur, past);
        }
        catch (Exception ex)
        {
            Log.Debug("对比历史报告失败：" + ex.Message);
            return null;
        }
    }

    public static PastSession Current(GameSession s)
    {
        var st = s.CountedStutters();
        return new PastSession(Clock.ToLocal(s.StartMs), s.DurationSec,
            st.Count(x => x.Noticeable) / Math.Max(s.DurationSec / 60, 0.01), s.AvgFps, s.Input.Connection);
    }

    public static Finding? Compare(PastSession cur, List<PastSession> past)
    {
        if (cur.DurationSec < MinSec || cur.Connection == "none") return null;
        // 最近 30 天内、换了一种连接方式的最近一局
        var prev = past.Where(p => p.Start < cur.Start && cur.Start - p.Start < TimeSpan.FromDays(30)
                                   && p.DurationSec >= MinSec && p.Connection != "none" && p.Connection != cur.Connection)
            .OrderByDescending(p => p.Start).FirstOrDefault();
        if (prev == null) return null;

        var (bt, other) = cur.Connection == "bluetooth" ? (cur, prev) : prev.Connection == "bluetooth" ? (prev, cur) : (null, null);
        if (bt == null || other == null) return null;
        // 蓝牙那局明显卡顿至少每分钟 1 次，且是另一局的 3 倍以上，才认为差别不是偶然
        if (bt.NoticeablePerMin < 1 || bt.NoticeablePerMin < Math.Max(other.NoticeablePerMin, 0.1) * 3) return null;

        bool curIsBt = ReferenceEquals(bt, cur);
        double fpsDiff = Math.Abs(bt.AvgFps - other.AvgFps) / Math.Max(1, Math.Max(bt.AvgFps, other.AvgFps));
        string when = prev.Start.Date == cur.Start.Date ? $"今天 {prev.Start:HH:mm}" : $"{prev.Start:M月d日 HH:mm}";
        string rates = $"手柄用蓝牙时每分钟 {bt.NoticeablePerMin:F1} 次明显卡顿，用{InputInfo.ConnectionName(other.Connection)}时每分钟 {other.NoticeablePerMin:F1} 次";
        string fps = fpsDiff <= 0.2
            ? $"两局平均帧率接近（{bt.AvgFps:F0} 和 {other.AvgFps:F0} FPS），画面负载差别不大，蓝牙连接基本可以确定是卡顿的主要来源。"
            : $"不过两局平均帧率差别较大（{bt.AvgFps:F0} 和 {other.AvgFps:F0} FPS），画质设置或游玩场景可能不同，这个对比仅供参考。";

        return new Finding
        {
            Key = "compare_input",
            Severity = curIsBt ? Severity.Critical : Severity.Info,
            Title = curIsBt ? "和上一局对比：蓝牙连接手柄时卡顿明显更多" : "和上一局对比：换掉蓝牙后卡顿明显减少",
            Detail = $"和{when}那一局对比，{rates}。{fps}",
            Advice = curIsBt
                ? new List<Tip>
                {
                    Easy("优先用数据线或接收器连接手柄。"),
                    Hands("必须用蓝牙的话，参考下面“蓝牙手柄延迟”里的建议逐项排查。"),
                }
                : new List<Tip> { Easy("继续用数据线或接收器连接手柄。") },
            Tech = $"本局 {cur.Connection} {cur.NoticeablePerMin:F2}/min {cur.AvgFps:F0}FPS {cur.DurationSec:F0}s；"
                 + $"对比局 {prev.Start:yyyy-MM-dd HH:mm} {prev.Connection} {prev.NoticeablePerMin:F2}/min {prev.AvgFps:F0}FPS {prev.DurationSec:F0}s",
        };
    }

    private static PastSession? Load(string path, string exe)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("game", out var g) || !g.TryGetProperty("Exe", out var e)
                || !string.Equals(e.GetString(), exe, StringComparison.OrdinalIgnoreCase)) return null;
            if (!root.TryGetProperty("input", out var input) || !input.TryGetProperty("connection", out var conn)) return null;
            return new PastSession(
                root.GetProperty("start").GetDateTime(),
                root.GetProperty("durationSec").GetDouble(),
                root.GetProperty("noticeablePerMin").GetDouble(),
                root.GetProperty("avgFps").GetDouble(),
                conn.GetString() ?? "none");
        }
        catch { return null; }
    }
}

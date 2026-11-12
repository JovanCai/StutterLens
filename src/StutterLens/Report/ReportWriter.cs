using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using StutterLens.Analysis;
using StutterLens.Core;

namespace StutterLens.Report;

public sealed class SessionReport
{
    public GameSession Session { get; init; } = null!;
    public List<Finding> Findings { get; init; } = new();
    public List<Finding> ConfigFindings { get; init; } = new();
    public List<(string Driver, long Count, double TotalMs, double MaxMs)> DpcTop { get; init; } = new();
    public double SelfCpuAvg { get; init; }
    public double SelfCpuMax { get; init; }
    public long SelfMemMB { get; init; }
    public int EventsLost { get; init; }
    public List<string> Notes { get; init; } = new();
}

public static class ReportWriter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StutterLens", "Reports");

    /// <summary>写出 HTML 和 JSON，返回 HTML 路径。</summary>
    public static string Write(SessionReport r, string dir)
    {
        Directory.CreateDirectory(dir);
        var s = r.Session;
        var safeName = string.Concat(Path.GetFileNameWithoutExtension(s.Exe).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var stamp = Clock.ToLocal(s.StartMs).ToString("yyyyMMdd_HHmmss");
        var basePath = Path.Combine(dir, $"{stamp}_{safeName}");
        // 不带 BOM：带 BOM 的 JSON 会让 Python 等常用解析器直接报错
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(basePath + ".json", ToJson(r), utf8);
        File.WriteAllText(basePath + ".html", ToHtml(r), utf8);
        return basePath + ".html";
    }

    public static string ToJson(SessionReport r)
    {
        var s = r.Session;
        var stutters = s.CountedStutters();
        var obj = new
        {
            game = new { s.Exe, s.Title, ImagePath = MaskUser(s.ImagePath), s.KnownAntiCheat },
            mode = new { requested = s.RequestedMode.ToString(), effective = s.EffectiveMode.ToString(), kernelTracingUsed = s.KernelUsed, antiCheatsSeen = s.AntiCheatsSeen.ToList() },
            start = Clock.ToLocal(s.StartMs),
            durationSec = Math.Round(s.DurationSec, 1),
            frames = s.Frames,
            avgFps = Math.Round(s.AvgFps, 1),
            low1Fps = Math.Round(s.Low1Fps, 1),
            low01Fps = Math.Round(s.Low01Fps, 1),
            stutters = stutters.Select(x => new
            {
                time = Clock.ToLocal(x.T),
                frameMs = Math.Round(x.FrameMs, 1),
                durationMs = Math.Round(x.DurationMs, 1),
                medianMs = Math.Round(x.MedianMs, 1),
                level = x.Level,
                causes = x.Causes.Select(c => new { c.Key, c.Title, c.Evidence, c.Tech, confidence = CauseAnalyzer.Confidence(c.Score), score = Math.Round(c.Score, 2) }),
            }),
            findings = r.Findings,
            config = r.ConfigFindings,
            dpcTop = r.DpcTop.Take(10).Select(d => new { d.Driver, d.Count, totalMs = Math.Round(d.TotalMs, 1), maxMs = Math.Round(d.MaxMs, 3) }),
            overhead = new { cpuAvgPct = Math.Round(r.SelfCpuAvg, 2), cpuMaxPct = Math.Round(r.SelfCpuMax, 2), memMB = r.SelfMemMB, r.EventsLost },
            notes = r.Notes,
            environment = new
            {
                app = typeof(ReportWriter).Assembly.GetName().Version?.ToString(3),
                os = Environment.OSVersion.VersionString,
                osArch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                processArch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                logicalCores = Environment.ProcessorCount,
            },
        };
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        });
    }

    /// <summary>用户可能把报告贴到网上求助，路径里的 Windows 用户名要去掉。</summary>
    public static string? MaskUser(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        var user = Environment.UserName;
        if (user.Length >= 3) text = text.Replace(user, "<user>", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string F(double v, int d = 0) => v.ToString("F" + d, Inv);

    public static string ToHtml(SessionReport r)
    {
        var s = r.Session;
        var stutters = s.CountedStutters();
        double minutes = Math.Max(s.DurationSec / 60, 0.01);
        int big = stutters.Count(x => x.Noticeable);
        int minor = stutters.Count - big;
        double bigPerMin = big / minutes;
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>{H(s.Title)} 卡顿报告</title><style>{Css}</style></head><body><main>");

        // ---------- 头部 ----------
        sb.Append($"<header><div class=\"eyebrow\">StutterLens 卡顿诊断报告</div><h1>{H(s.Title)}</h1>");
        sb.Append($"<p class=\"meta\">{Clock.ToLocal(s.StartMs):yyyy-MM-dd HH:mm} · 时长 {FormatDuration(s.DurationSec)} · ");
        sb.Append(s.EffectiveMode == MonitorMode.Safe
            ? "<span class=\"pill safe\" title=\"检测到反作弊，仅读取系统公开数据，不触碰游戏进程\">反作弊安全模式</span>"
            : "<span class=\"pill full\" title=\"未检测到反作弊，已启用内核级驱动与磁盘追踪\">深度模式</span>");
        sb.Append("</p></header>");

        // ---------- 一句话结论 ----------
        // 结论只看“明显卡顿”（画面停住超过 0.05 秒），轻微顿挫只做补充说明
        var (verdictCls, verdict) = big == 0 && minor == 0 ? ("good", "整体流畅，未检测到卡顿。")
            : big == 0 ? ("good", "整体流畅，仅有少量轻微顿挫。")
            : bigPerMin < 0.5 ? ("ok", "偶有卡顿，整体流畅。")
            : bigPerMin < 2 ? ("warn", "卡顿较频繁，建议按下方分析逐项排查。")
            : ("bad", "卡顿频繁，建议优先处理下方排在前面的原因。");
        sb.Append($"<p class=\"verdict {verdictCls}\">{verdict}</p>");

        sb.Append("<section class=\"kpis\">");
        Kpi(sb, "平均帧率", F(s.AvgFps), "FPS");
        Kpi(sb, "1% Low", F(s.Low1Fps), "最慢 1% 帧对应的帧率，越接近平均帧率越稳定");
        Kpi(sb, "明显卡顿", big.ToString(), minor > 0
            ? $"停顿超过 50ms 的次数，另有 {minor} 次轻微顿挫"
            : "停顿超过 50ms 的次数");
        sb.Append("</section>");

        // ---------- 问题和解决办法 ----------
        sb.Append("<section><h2>原因分析与建议</h2>");
        sb.Append("<p class=\"muted\">按影响从大到小排列，建议按难度排序，可以从“简单”开始尝试。</p>");
        foreach (var f in r.Findings) FindingCard(sb, f);
        sb.Append("</section>");

        // ---------- 曲线 ----------
        sb.Append("<section><h2>帧时间曲线</h2><p class=\"muted\">纵轴是帧时间，越低越流畅。红点为检测到的卡顿，悬停可查看原因。</p>");
        sb.Append(Chart(s, stutters));
        sb.Append("</section>");

        // ---------- 卡顿记录 ----------
        if (stutters.Count > 0)
        {
            sb.Append("<section><h2>卡顿记录</h2><p class=\"muted\">按停顿时长列出前 30 次。原因根据卡顿时刻的系统状态推测，“把握”表示推测的可信度。</p>");
            sb.Append("<div class=\"tablewrap\"><table><thead><tr><th>时间</th><th>停顿</th><th>最可能的原因</th><th>把握</th></tr></thead><tbody>");
            foreach (var x in stutters.OrderByDescending(x => x.SpanMs).Take(30))
            {
                var p = x.Primary;
                var conf = CauseAnalyzer.Confidence(p?.Score ?? 0);
                sb.Append($"<tr><td class=\"num\">{Clock.ToLocal(x.T):HH:mm:ss}</td><td class=\"num\"><b>{FormatMs(x.SpanMs)}</b><div class=\"muted small\">{(x.Noticeable ? "明显" : "轻微")}</div></td>");
                sb.Append($"<td>{H(p?.Title)}<div class=\"muted small\">{H(p?.Evidence)}</div></td><td><span class=\"conf c{conf}\">{conf}</span></td></tr>");
            }
            sb.Append("</tbody></table></div></section>");
        }

        // ---------- 电脑设置检查 ----------
        if (r.ConfigFindings.Count > 0)
        {
            sb.Append("<section><h2>系统设置检查</h2><p class=\"muted\">与本次卡顿不一定直接相关，是同时检查出的系统设置问题。</p>");
            foreach (var f in r.ConfigFindings.OrderByDescending(f => f.Severity)) FindingCard(sb, f);
            sb.Append("</section>");
        }

        // ---------- 技术附录 ----------
        sb.Append("<footer><details><summary>技术细节</summary><div class=\"tech\">");
        sb.Append(s.EffectiveMode == MonitorMode.Safe
            ? "<p>采集方式：反作弊安全模式。只用了 Windows 公开的性能计数器、进程列表和图形 Present 事件，没有开启内核追踪。StutterLens 没有打开游戏进程的句柄，也没有注入或读取游戏内存。</p>"
            : "<p>采集方式：深度模式。在公开数据之外开启了内核 ETW（DPC/ISR 和磁盘 IO），同样没有打开游戏进程的句柄，也没有注入或读取游戏内存。</p>");
        sb.Append($"<p>帧数据：共 {s.Frames:N0} 帧，平均 {F(s.AvgFps, 1)} FPS，1% Low {F(s.Low1Fps, 1)} FPS，0.1% Low {F(s.Low01Fps, 1)} FPS。</p>");
        sb.Append($"<p>StutterLens 自身开销：CPU 平均 {F(r.SelfCpuAvg, 2)}%，峰值 {F(r.SelfCpuMax, 2)}%，内存约 {r.SelfMemMB}MB。</p>");
        if (r.EventsLost > 0) sb.Append($"<p class=\"bad\">采集中丢失了 {r.EventsLost} 个 ETW 事件，部分帧时间可能不准。</p>");
        foreach (var n in r.Notes) sb.Append($"<p>{H(n)}</p>");
        if (r.DpcTop.Count > 0)
        {
            sb.Append("<p>驱动 DPC/ISR 排行（整个会话累计，单次超过 1ms 值得关注）：</p><div class=\"tablewrap\"><table><thead><tr><th>驱动文件</th><th>硬件</th><th>次数</th><th>累计</th><th>单次最长</th></tr></thead><tbody>");
            foreach (var d in r.DpcTop.Take(10))
                sb.Append($"<tr><td>{H(d.Driver)}</td><td>{H(Knowledge.KnowledgeBase.DeviceForDriver(d.Driver).Device)}</td><td class=\"num\">{d.Count:N0}</td><td class=\"num\">{F(d.TotalMs)}ms</td><td class=\"num{(d.MaxMs >= 1 ? " bad" : "")}\">{F(d.MaxMs, 3)}ms</td></tr>");
            sb.Append("</tbody></table></div>");
        }
        var techStutters = stutters.OrderByDescending(x => x.SpanMs).Take(30).Where(x => x.Primary != null && x.Primary.Tech.Length > 0).ToList();
        if (techStutters.Count > 0)
        {
            sb.Append("<p>每次卡顿的原始数据：</p><ul class=\"rawlist\">");
            foreach (var x in techStutters)
                sb.Append($"<li>{Clock.ToLocal(x.T):HH:mm:ss}　最差帧 {F(x.FrameMs)}ms，合计 {F(x.SpanMs)}ms（基线 {F(x.MedianMs, 1)}ms）　{H(x.Primary!.Tech)}</li>");
            sb.Append("</ul>");
        }
        sb.Append("</div></details></footer></main></body></html>");
        return sb.ToString();
    }

    private static void Kpi(StringBuilder sb, string label, string value, string hint) =>
        sb.Append($"<div class=\"kpi\"><div class=\"label\">{label}</div><div class=\"value\">{value}</div><div class=\"hint\">{H(hint)}</div></div>");

    private static void FindingCard(StringBuilder sb, Finding f)
    {
        var cls = f.Severity switch { Severity.Critical => "crit", Severity.Warning => "warn", _ => "info" };
        var tag = f.Severity switch { Severity.Critical => "优先处理", Severity.Warning => "建议处理", _ => "供参考" };
        sb.Append($"<article class=\"card {cls}\"><div class=\"cardhead\"><span class=\"tag\">{tag}</span><h3>{H(f.Title)}</h3></div>");
        if (!string.IsNullOrEmpty(f.Detail)) sb.Append($"<p>{H(f.Detail)}</p>");
        if (f.Advice.Count > 0)
        {
            sb.Append("<div class=\"todo\">处理建议</div><ol class=\"advice\">");
            var order = new[] { "简单", "中等", "进阶" };
            foreach (var a in f.Advice.OrderBy(a => Array.IndexOf(order, a.Level)))
                sb.Append($"<li><span class=\"lv lv{Array.IndexOf(order, a.Level)}\">{H(a.Level)}</span>{H(a.Text)}</li>");
            sb.Append("</ol>");
        }
        if (!string.IsNullOrEmpty(f.Tech))
            sb.Append($"<details class=\"techbox\"><summary>技术细节</summary><pre>{H(f.Tech)}</pre></details>");
        sb.Append("</article>");
    }

    private static string Chart(GameSession s, List<Stutter> stutters)
    {
        var secs = s.SecondsSnapshot().Where(b => b.Frames > 0).ToList();
        if (secs.Count < 2) return "<p class=\"muted\">数据不足，无法绘制曲线。</p>";
        const int W = 960, Hh = 240, padL = 48, padB = 22, padT = 8;
        int maxSec = secs[^1].Sec;
        double maxMs = Math.Min(Math.Max(secs.Max(b => b.MaxMs), 20), 300);
        double X(double sec) => padL + sec / Math.Max(1, maxSec) * (W - padL - 8);
        double Y(double ms) => padT + (1 - Math.Min(ms, maxMs) / maxMs) * (Hh - padT - padB);

        var sb = new StringBuilder();
        sb.Append($"<svg class=\"chart\" viewBox=\"0 0 {W} {Hh}\" preserveAspectRatio=\"none\" role=\"img\" aria-label=\"帧时间曲线\">");
        foreach (var g in new[] { 1 / 4.0, 1 / 2.0, 3 / 4.0, 1.0 })
        {
            double ms = maxMs * g;
            sb.Append($"<line x1=\"{padL}\" x2=\"{W - 8}\" y1=\"{F(Y(ms), 1)}\" y2=\"{F(Y(ms), 1)}\" class=\"grid\"/><text x=\"{padL - 4}\" y=\"{F(Y(ms) + 4, 1)}\" class=\"axis\" text-anchor=\"end\">{F(ms)}ms</text>");
        }
        var area = new StringBuilder($"M{F(X(secs[0].Sec), 1)},{F(Y(0), 1)}");
        foreach (var b in secs) area.Append($" L{F(X(b.Sec), 1)},{F(Y(b.SumMs / b.Frames), 1)}");
        area.Append($" L{F(X(secs[^1].Sec), 1)},{F(Y(0), 1)} Z");
        sb.Append($"<path d=\"{area}\" class=\"avg\"/>");
        var line = new StringBuilder();
        foreach (var b in secs) line.Append(line.Length == 0 ? "M" : " L").Append($"{F(X(b.Sec), 1)},{F(Y(b.MaxMs), 1)}");
        sb.Append($"<path d=\"{line}\" class=\"max\"/>");
        foreach (var st in stutters)
        {
            double sec = (st.T - s.StartMs) / 1000;
            sb.Append($"<circle cx=\"{F(X(sec), 1)}\" cy=\"{F(Y(st.FrameMs), 1)}\" r=\"3.5\" class=\"dot\"><title>{Clock.ToLocal(st.T):HH:mm:ss} 停顿 {FormatMs(st.SpanMs)}：{H(st.Primary?.Title)}</title></circle>");
        }
        sb.Append($"<text x=\"{padL}\" y=\"{Hh - 6}\" class=\"axis\">0</text><text x=\"{W - 8}\" y=\"{Hh - 6}\" class=\"axis\" text-anchor=\"end\">{FormatDuration(maxSec)}</text>");
        sb.Append("</svg>");
        sb.Append("<p class=\"legend\"><span class=\"sw max\"></span>每秒最慢帧　<span class=\"sw avg\"></span>每秒平均帧时间　<span class=\"sw dot\"></span>卡顿</p>");
        return sb.ToString();
    }

    private static string FormatMs(double ms) => ms >= 1000 ? $"{F(ms / 1000, 1)}s" : $"{F(ms)}ms";

    private static string FormatDuration(double sec)
    {
        var t = TimeSpan.FromSeconds(sec);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} 小时 {t.Minutes} 分" : t.TotalMinutes >= 1 ? $"{t.Minutes} 分 {t.Seconds} 秒" : $"{t.Seconds} 秒";
    }

    private const string Css = @"
:root{--bg:#f6f5f2;--panel:#fff;--ink:#1d1d1b;--muted:#6b6a66;--line:#e4e2dc;--accent:#2f5d8a;--crit:#b3261e;--warn:#a15c00;--info:#2f5d8a;--ok:#2e7d4f;--area:rgba(47,93,138,.16);--chip:#efede7}
@media (prefers-color-scheme:dark){:root:not([data-theme=light]){--bg:#141413;--panel:#1d1d1b;--ink:#ecebe6;--muted:#9c9a93;--line:#2e2d2a;--accent:#86aee0;--crit:#f2867d;--warn:#e8b04c;--info:#86aee0;--ok:#7cc79a;--area:rgba(134,174,224,.18);--chip:#2a2926}}
:root[data-theme=dark]{--bg:#141413;--panel:#1d1d1b;--ink:#ecebe6;--muted:#9c9a93;--line:#2e2d2a;--accent:#86aee0;--crit:#f2867d;--warn:#e8b04c;--info:#86aee0;--ok:#7cc79a;--area:rgba(134,174,224,.18);--chip:#2a2926}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.7 system-ui,-apple-system,'Segoe UI','Microsoft YaHei',sans-serif}
main{max-width:1040px;margin:0 auto;padding:32px 16px 64px}
.eyebrow{font-size:12px;letter-spacing:.08em;color:var(--muted)}
h1{font-size:30px;margin:4px 0 6px}h2{font-size:19px;margin:40px 0 6px}h3{font-size:16px;margin:0}
.meta,.muted{color:var(--muted)}.small{font-size:13px}
.pill{display:inline-block;padding:1px 10px;border-radius:999px;font-size:13px;border:1px solid currentColor;cursor:help}.pill.safe{color:var(--ok)}.pill.full{color:var(--accent)}
.verdict{font-size:18px;font-weight:600;margin:20px 0 0;padding:12px 16px;border-radius:10px;background:var(--panel);border:1px solid var(--line)}
.verdict.good{color:var(--ok)}.verdict.ok{color:var(--info)}.verdict.warn{color:var(--warn)}.verdict.bad{color:var(--crit)}
.kpis{display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));gap:12px;margin-top:12px}
.kpi{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:14px 16px}
.kpi .label{color:var(--muted);font-size:13px}.kpi .value{font-size:30px;font-weight:650;font-variant-numeric:tabular-nums}.kpi .hint{color:var(--muted);font-size:12px;line-height:1.5}
.chart{width:100%;height:240px;background:var(--panel);border:1px solid var(--line);border-radius:10px}
.chart .grid{stroke:var(--line);stroke-width:1}.chart .axis{fill:var(--muted);font-size:11px}
.chart .avg{fill:var(--area);stroke:none}.chart .max{fill:none;stroke:var(--accent);stroke-width:1.4;vector-effect:non-scaling-stroke}.chart .dot{fill:var(--crit)}
.legend{color:var(--muted);font-size:13px}.sw{display:inline-block;width:14px;height:10px;margin-right:4px;vertical-align:middle;border-radius:2px}.sw.max{background:var(--accent);height:2px}.sw.avg{background:var(--area)}.sw.dot{background:var(--crit);width:8px;height:8px;border-radius:50%}
.card{background:var(--panel);border:1px solid var(--line);border-left:4px solid var(--info);border-radius:10px;padding:14px 16px;margin:12px 0}
.card.crit{border-left-color:var(--crit)}.card.warn{border-left-color:var(--warn)}
.cardhead{display:flex;gap:10px;align-items:baseline;flex-wrap:wrap}.tag{font-size:12px;font-weight:600;color:var(--info)}.crit .tag{color:var(--crit)}.warn .tag{color:var(--warn)}
.card p{margin:6px 0}.todo{font-weight:600;margin-top:10px}
.advice{margin:4px 0 0;padding-left:22px}.advice li{margin:4px 0}
.lv{display:inline-block;font-size:12px;padding:0 7px;margin-right:8px;border-radius:4px;background:var(--chip);color:var(--muted)}.lv0{color:var(--ok)}.lv1{color:var(--warn)}.lv2{color:var(--crit)}
details summary{cursor:pointer;color:var(--muted);font-size:13px}.techbox{margin-top:8px}
pre{white-space:pre-wrap;font-size:12px;color:var(--muted);margin:6px 0 0}
.tablewrap{overflow-x:auto;background:var(--panel);border:1px solid var(--line);border-radius:10px}
table{border-collapse:collapse;width:100%;font-size:14px}th,td{text-align:left;padding:8px 12px;border-bottom:1px solid var(--line);vertical-align:top}th{color:var(--muted);font-weight:500}
tr:last-child td{border-bottom:none}.num{font-variant-numeric:tabular-nums;white-space:nowrap}.bad{color:var(--crit)}
.conf{font-size:13px;padding:0 8px;border-radius:4px;background:var(--chip)}.c高{color:var(--ok)}.c中{color:var(--warn)}.c低{color:var(--muted)}
footer{margin-top:48px;color:var(--muted);font-size:14px}footer summary{font-size:14px}.tech{margin-top:8px}.rawlist{font-size:12px;padding-left:18px}
";
}

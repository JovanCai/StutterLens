using StutterLens.Analysis;
using StutterLens.Collectors;
using StutterLens.Core;
using StutterLens.Knowledge;
using StutterLens.Report;

namespace StutterLens.Tests;

public class AnalysisTests
{
    private static GameSession Session() => new() { Pid = 4242, Exe = "game.exe", Title = "Game", StartMs = 0, EffectiveMode = MonitorMode.Full };

    private static Stutter MakeStutter(double t, double ms) =>
        new() { T = t, FrameMs = ms, DurationMs = ms, MedianMs = 16.7 };

    [Fact]
    public void NoEvidence_FallsBackToGameInternal()
    {
        var an = new CauseAnalyzer(new SignalStore());
        var hits = an.Analyze(MakeStutter(50_000, 80), Session(), kernel: false);
        Assert.Equal("game_internal", hits[0].Key);
    }

    [Fact]
    public void FocusChange_IsTopCause()
    {
        var store = new SignalStore();
        store.Focus.Add(new FocusEvent(49_900, 999));
        var hits = new CauseAnalyzer(store).Analyze(MakeStutter(50_000, 80), Session(), kernel: false);
        Assert.Equal("focus", hits[0].Key);
    }

    [Fact]
    public void LongDpcOnOneCore_IsAttributedToDeviceNotFileName()
    {
        var store = new SignalStore();
        store.Drivers.Add(0xFFFF_F800_0000_0000UL, 0x100000, @"\SystemRoot\System32\drivers\nvlddmkm.sys");
        int idx = store.Drivers.Lookup(0xFFFF_F800_0000_1000UL);
        store.AddDpc(49_960, 3.0, idx, isr: false, cpu: 0);
        var hits = new CauseAnalyzer(store).Analyze(MakeStutter(50_000, 80), Session(), kernel: true);
        var dpc = hits.First(h => h.Key.StartsWith("dpc:"));
        Assert.Equal("dpc:gpu", dpc.Key);
        Assert.DoesNotContain(".sys", dpc.Title);
        Assert.Contains("nvlddmkm.sys", dpc.Tech);
    }

    [Fact]
    public void ShortDpcsSpreadAcrossCores_AreNotBlamed()
    {
        var store = new SignalStore();
        store.Drivers.Add(0xFFFF_F800_0000_0000UL, 0x100000, "dxgkrnl.sys");
        int idx = store.Drivers.Lookup(0xFFFF_F800_0000_1000UL);
        for (int cpu = 0; cpu < 16; cpu++) store.AddDpc(49_960, 0.2, idx, isr: false, cpu: cpu);
        var hits = new CauseAnalyzer(store).Analyze(MakeStutter(50_000, 30), Session(), kernel: true);
        Assert.DoesNotContain(hits, h => h.Key.StartsWith("dpc:"));
    }

    [Fact]
    public void IntegratedGpuSmallDedicatedVram_IsNotReportedAsFull()
    {
        var store = new SignalStore();
        // 核显：专用显存只登记了 512MB，占用率接近 100% 是常态
        store.Sys.Add(new SysSample { T = 50_000, VramTotalMB = 512, VramUsedMB = 510 });
        var hits = new CauseAnalyzer(store).Analyze(MakeStutter(49_800, 80), Session(), kernel: false);
        Assert.DoesNotContain(hits, h => h.Key == "vram_full");
    }

    [Fact]
    public void DiscreteGpuFullVram_IsReported()
    {
        var store = new SignalStore();
        store.Sys.Add(new SysSample { T = 50_000, VramTotalMB = 8192, VramUsedMB = 8100 });
        var hits = new CauseAnalyzer(store).Analyze(MakeStutter(49_800, 80), Session(), kernel: false);
        Assert.Contains(hits, h => h.Key == "vram_full");
    }

    [Theory]
    [InlineData("qcdxkmsuc8380.sys", "gpu")]
    [InlineData("nvlddmkm.sys", "gpu")]
    [InlineData("amdkmdag.sys", "gpu")]
    [InlineData("ndis.sys", "net")]
    [InlineData("rt640x64.sys", "net")]
    [InlineData("RTCore64.sys", "other")]   // 小飞机的驱动，不能被误认成 Realtek 网卡
    [InlineData("USBXHCI.SYS", "usb")]
    [InlineData("whatever.sys", "other")]
    public void DriverFiles_MapToDeviceCategories(string file, string device) =>
        Assert.Equal(device, KnowledgeBase.DeviceForDriver(file).Key);

    [Fact]
    public void MinorStuttersOnly_DoNotProduceAlarmingOverview()
    {
        var s = Session();
        for (int i = 0; i < 20; i++)
            s.Stutters.Add(new Stutter { T = 20_000 + i * 1000, FrameMs = 30, DurationMs = 30, MedianMs = 11, Analyzed = true,
                Causes = { new CauseHit { Key = "game_internal", Title = "x", Score = 0.35 } } });
        s.EndMs = 80_000;
        var findings = new CauseAnalyzer(new SignalStore()).Summarize(s);
        Assert.Equal("overview", findings[0].Key);
        Assert.Equal(Severity.Info, findings[0].Severity);
        Assert.All(findings, f => Assert.NotEqual(Severity.Critical, f.Severity));
    }

    [Fact]
    public void ReportJson_MasksUserProfilePath()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var masked = ReportWriter.MaskUser(Path.Combine(profile, "Games", "game.exe"))!;
        Assert.DoesNotContain(profile, masked, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("%USERPROFILE%", masked);
    }

    [Fact]
    public void ReportHtml_RendersWithoutDriverFileNamesInMainBody()
    {
        var store = new SignalStore();
        store.Drivers.Add(0xFFFF_F800_0000_0000UL, 0x100000, "nvlddmkm.sys");
        int idx = store.Drivers.Lookup(0xFFFF_F800_0000_1000UL);
        store.AddDpc(49_960, 3.0, idx, isr: false, cpu: 0);
        var an = new CauseAnalyzer(store);
        var s = Session();
        var st = MakeStutter(50_000, 120);
        st.Causes = an.Analyze(st, s, kernel: true);
        st.Analyzed = true;
        s.Stutters.Add(st);
        s.EndMs = 60_000;
        var html = ReportWriter.ToHtml(new SessionReport { Session = s, Findings = an.Summarize(s) });
        int techStart = html.IndexOf("<footer>", StringComparison.Ordinal);
        var body = html[..techStart];
        Assert.Contains("显卡驱动延迟过高", body);
        // 驱动文件名只允许出现在折叠的技术细节里
        var outsideDetails = System.Text.RegularExpressions.Regex.Replace(body, "<details.*?</details>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.DoesNotContain("nvlddmkm", outsideDetails);
    }
}

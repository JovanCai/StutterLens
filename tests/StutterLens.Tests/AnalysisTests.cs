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
    [InlineData("Wdf01000.sys", "usb")]
    [InlineData("BTHport.sys", "bluetooth")]
    [InlineData("ibtusb.sys", "bluetooth")]
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

    // ---------------- 蓝牙手柄与对照时段 ----------------

    private static void SlowReadsEveryHalfSecond(SignalStore store, int pid, double until)
    {
        for (double t = 0; t <= until; t += 500)
            store.Disk.Add(new DiskSample(t, 60, pid, 32 * 1024, false));
    }

    [Fact]
    public void DiskReadThatAlsoHappensWithoutStutters_IsDownweighted()
    {
        var store = new SignalStore();
        var sess = Session();
        SlowReadsEveryHalfSecond(store, sess.Pid, 51_000);
        var hits = new CauseAnalyzer(store).Analyze(MakeStutter(50_000, 80), sess, kernel: true);
        var disk = hits.First(h => h.Key == "disk_io");
        Assert.True(disk.Score < 0.4, $"score {disk.Score}");
        Assert.Contains("关联不强", disk.Evidence);
        Assert.Contains("对照", disk.Tech);
    }

    [Fact]
    public void DiskReadOnlyDuringStutter_KeepsItsScore()
    {
        var store = new SignalStore();
        var sess = Session();
        store.Disk.Add(new DiskSample(49_950, 120, sess.Pid, 64 * 1024, false));
        var hits = new CauseAnalyzer(store).Analyze(MakeStutter(50_000, 80), sess, kernel: true);
        Assert.Equal("disk_io", hits[0].Key);
        Assert.True(hits[0].Score >= 0.75);
    }

    [Fact]
    public void BluetoothPadConnected_AndNothingElseSpecific_PointsToBluetooth()
    {
        var store = new SignalStore();
        var sess = Session();
        SlowReadsEveryHalfSecond(store, sess.Pid, 51_000);
        store.Sys.Add(new SysSample { T = 50_500, BtPadConnected = true, AvailMB = 8000, CommitPct = 50 });
        store.NoteModule(sess.Pid, @"C:\Windows\System32\XInput1_4.dll");
        var hits = new CauseAnalyzer(store).Analyze(MakeStutter(50_000, 80), sess, kernel: true);
        Assert.Equal("bt_gamepad", hits[0].Key);
        Assert.Equal("低", CauseAnalyzer.Confidence(hits[0].Score)); // 没有直接证据，把握只能是低
    }

    [Fact]
    public void GameWithoutGamepadApi_IsNotBlamedOnBluetoothPad()
    {
        var store = new SignalStore();
        var sess = Session();
        store.Sys.Add(new SysSample { T = 50_500, BtPadConnected = true, AvailMB = 8000, CommitPct = 50 });
        store.NoteModule(sess.Pid, @"C:\Windows\System32\d3d11.dll");
        var hits = new CauseAnalyzer(store).Analyze(MakeStutter(50_000, 80), sess, kernel: true);
        Assert.DoesNotContain(hits, h => h.Key == "bt_gamepad");
    }

    [Fact]
    public void BluetoothDisconnectNearStutter_IsTopCause()
    {
        var store = new SignalStore();
        var pad = new StutterLens.Platform.InputDevice("Xbox Wireless Controller", StutterLens.Platform.InputKind.Gamepad,
            StutterLens.Platform.Transport.Bluetooth, "{c1}", "x");
        store.Input.Add(new InputEvent(49_200, pad, false));
        store.Input.Add(new InputEvent(49_900, pad, true));
        var hits = new CauseAnalyzer(store).Analyze(MakeStutter(50_000, 300), Session(), kernel: false);
        Assert.Equal("bt_link", hits[0].Key);
        Assert.Contains("断开后又重新连上", hits[0].Evidence);
    }

    [Fact]
    public void ReceiverMouseDroppingNearStutter_IsReportedAsInputChange()
    {
        var store = new SignalStore();
        var mouse = new StutterLens.Platform.InputDevice("USB Receiver", StutterLens.Platform.InputKind.Mouse,
            StutterLens.Platform.Transport.Receiver, @"USB\VID_046D&PID_C547", "m");
        store.Input.Add(new InputEvent(49_500, mouse, false));
        var hits = new CauseAnalyzer(store).Analyze(MakeStutter(50_000, 200), Session(), kernel: false);
        Assert.Equal("input_change", hits[0].Key);
        Assert.Contains("鼠标，无线接收器", hits[0].Evidence);
    }

    [Fact]
    public void Summary_AddsBluetoothContextAndTips()
    {
        var s = Session();
        for (int i = 0; i < 10; i++)
            s.Stutters.Add(new Stutter { T = 20_000 + i * 3000, FrameMs = 90, DurationMs = 90, MedianMs = 16.7, Analyzed = true,
                Causes = { new CauseHit { Key = "disk_io", Title = "硬盘读取阻塞", Score = 0.3 } } });
        s.EndMs = 80_000;
        var pad = new StutterLens.Platform.InputDevice("Xbox Wireless Controller", StutterLens.Platform.InputKind.Gamepad,
            StutterLens.Platform.Transport.Bluetooth, "{c1}", @"HID\{00001124-0000-1000-8000-00805F9B34FB}&VID_045E&PID_02E0&IG_00\8&1");
        var snap = new StutterLens.Platform.InputSnapshot
        {
            Devices = { pad },
            Radio = new("Generic Bluetooth Radio", @"USB\VID_0A12&PID_0001&1", 6),
            WifiChannel = 6,
        };
        for (int i = 0; i < 60; i++) s.Input.Update(snap);
        var findings = new CauseAnalyzer(new SignalStore()).Summarize(s);
        var bt = findings.Single(f => f.Key == "bt_gamepad");
        Assert.Equal(Severity.Warning, bt.Severity);
        Assert.Contains("蓝牙 4.0", bt.Detail);
        Assert.Contains("2.4GHz", bt.Detail);
        Assert.Contains(bt.Advice, t => t.Text.Contains("CSR"));
        Assert.Contains(bt.Advice, t => t.Text.Contains("Xbox 配件"));
        Assert.Contains(bt.Advice, t => t.Text.Contains("5GHz"));
        Assert.Equal("bluetooth", s.Input.Connection);
    }

    [Fact]
    public void ComparedWithReceiverSession_BluetoothIsBlamed()
    {
        var now = new DateTime(2026, 11, 12, 22, 30, 0);
        var cur = new SessionComparer.PastSession(now, 400, 13.7, 55, "bluetooth");
        var past = new List<SessionComparer.PastSession>
        {
            new(now.AddMinutes(-40), 420, 0.4, 58, "usb"),
            new(now.AddDays(-40), 420, 0.1, 58, "usb"), // 太久以前，不参与对比
        };
        var f = SessionComparer.Compare(cur, past);
        Assert.NotNull(f);
        Assert.Equal(Severity.Critical, f!.Severity);
        Assert.Contains("13.7", f.Detail);
        Assert.Contains("基本可以确定", f.Detail);
    }

    [Fact]
    public void ComparedSessionsWithSimilarRates_ProduceNoFinding()
    {
        var now = new DateTime(2026, 11, 12, 22, 30, 0);
        var cur = new SessionComparer.PastSession(now, 400, 2.0, 55, "bluetooth");
        var past = new List<SessionComparer.PastSession> { new(now.AddHours(-1), 400, 1.5, 55, "usb") };
        Assert.Null(SessionComparer.Compare(cur, past));
    }

    // ---------------- 显卡 ----------------

    private static void SteadyHistory(SignalStore store, Action<SysSample>? tweak = null)
    {
        for (double t = 19_000; t <= 48_000; t += 1000)
        {
            var smp = new SysSample { T = t, GpuTargetUtil = 70, VramUsedMB = 7000, VramTotalMB = 8192, VramSharedMB = 400, AvailMB = 8000, CommitPct = 50 };
            smp.GpuProcs.Add(new ProcCpu(900, "chrome.exe", 3));
            tweak?.Invoke(smp);
            store.Sys.Add(smp);
        }
    }

    [Fact]
    public void GpuJumpingToFullLoad_IsGpuSpike()
    {
        var store = new SignalStore();
        SteadyHistory(store);
        store.Sys.Add(new SysSample { T = 50_500, GpuTargetUtil = 99, VramUsedMB = 7000, VramTotalMB = 8192, VramSharedMB = 400, AvailMB = 8000, CommitPct = 50 });
        var hits = new CauseAnalyzer(store).Analyze(MakeStutter(50_000, 80), Session(), kernel: false);
        Assert.Equal("gpu_spike", hits[0].Key);
        Assert.Contains(hits[0].Advice, t => t.Text.Contains("阴影"));
    }

    [Fact]
    public void BackgroundProcessGrabbingGpu_IsReported()
    {
        var store = new SignalStore();
        SteadyHistory(store);
        var now = new SysSample { T = 50_500, GpuTargetUtil = 60, VramUsedMB = 7000, VramTotalMB = 8192, VramSharedMB = 400, AvailMB = 8000, CommitPct = 50 };
        now.GpuProcs.Add(new ProcCpu(900, "chrome.exe", 55));
        store.Sys.Add(now);
        var hits = new CauseAnalyzer(store).Analyze(MakeStutter(50_000, 80), Session(), kernel: false);
        Assert.Equal("bggpu:chrome.exe", hits[0].Key);
        Assert.Contains("Chrome", hits[0].Title);
    }

    [Fact]
    public void VramSpillingIntoSharedMemory_IsVramFull()
    {
        var store = new SignalStore();
        SteadyHistory(store);
        store.Sys.Add(new SysSample { T = 50_500, GpuTargetUtil = 80, VramUsedMB = 7600, VramTotalMB = 8192, VramSharedMB = 1400, AvailMB = 8000, CommitPct = 50 });
        var hits = new CauseAnalyzer(store).Analyze(MakeStutter(50_000, 80), Session(), kernel: false);
        var v = hits.First(h => h.Key == "vram_full");
        Assert.Contains("借用内存", v.Evidence);
    }

    [Fact]
    public void FrameCapAdvice_GetsConcreteNumber()
    {
        var s = Session();
        for (int i = 0; i < 6; i++)
            s.Stutters.Add(new Stutter { T = 20_000 + i * 5000, FrameMs = 90, DurationMs = 90, MedianMs = 16.7, Analyzed = true,
                Causes = { new CauseHit { Key = "gpu_spike", Title = "显卡瞬时满载", Score = 0.5,
                    Advice = CauseAnalyzer.Catalog["gpu_spike"].Tips.ToList() } } });
        for (int i = 0; i < 6000; i++) s.OnFrame(10_000 + i * 12.5, 12.5, () => true); // 80 FPS
        s.EndMs = 85_000;
        var f = new CauseAnalyzer(new SignalStore()).Summarize(s).Single(x => x.Key == "gpu_spike");
        Assert.Contains(f.Advice, t => t.Text.Contains("可以先试 70 FPS"));
    }

    [Fact]
    public void Prune_KeepsNewestSessionsAndIgnoresOtherFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sl-prune-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 1; i <= 5; i++)
                foreach (var ext in new[] { ".html", ".json" })
                    File.WriteAllText(Path.Combine(dir, $"2026111{i}_120000_Game{ext}"), "");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "");
            Assert.Equal(2, Settings.Prune(dir, 3));
            var left = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(x => x).ToList();
            Assert.Equal(7, left.Count);
            Assert.Contains("notes.txt", left);
            Assert.DoesNotContain("20261111_120000_Game.html", left);
        }
        finally { Directory.Delete(dir, true); }
    }
}

using System.Runtime.InteropServices;
using StutterLens.Core;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace StutterLens.Collectors;

/// <summary>
/// ETW 采集器（与 PresentMon 同源的系统事件跟踪）。
///
/// 两个独立会话：
///  1. 图形会话（始终开启）：只订阅 DXGI / D3D9 / DxgKrnl 的 Present 类事件（按事件 ID 过滤），
///     用来计算每个进程的帧时间。这是 Windows 自带的事件广播，游戏进程不会感知到被“读取”。
///  2. 内核会话（仅深度模式）：DPC/ISR + 磁盘 IO + 映像加载，用于定位驱动延迟和磁盘阻塞。
///     检测到反作弊时会被立即关闭，且在反作弊存在时永不开启。
/// </summary>
public sealed class EtwCollector : IDisposable
{
    public const string PresentSessionName = "StutterLens-Present";
    public const string KernelSessionName = "StutterLens-Kernel";

    public static readonly Guid DxgiGuid = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    public static readonly Guid D3D9Guid = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    public static readonly Guid DxgKrnlGuid = new("802EC45A-1E99-4B83-9920-87C98277BA9D");

    /// <summary>非 DXGI/D3D9 来源（内核 PresentHistory）的“伪交换链”标记位。</summary>
    public const ulong KernelSourceFlag = 0xFFFF_0000_0000_0000UL;

    /// <summary>(pid, 交换链键, 时间ms)</summary>
    public event Action<int, ulong, double>? Present;

    private readonly SignalStore _store;
    private TraceEventSession? _presentSession;
    private TraceEventSession? _kernelSession;
    private readonly object _kernelGate = new();
    private double _presentOffset = double.NaN, _kernelOffset = double.NaN;

    public EtwCollector(SignalStore store) => _store = store;

    public bool KernelActive { get { lock (_kernelGate) return _kernelSession != null; } }
    public int PresentEventsLost => _presentSession?.EventsLost ?? 0;
    public int KernelEventsLost { get; private set; }
    public string? KernelError { get; private set; }

    public static bool IsElevated => TraceEventSession.IsElevated() == true;

    /// <summary>清理上次异常退出时遗留的同名会话。</summary>
    public static void CleanupStaleSessions()
    {
        foreach (var name in new[] { PresentSessionName, KernelSessionName })
        {
            try
            {
                if (TraceEventSession.GetActiveSessionNames().Contains(name))
                    TraceEventSession.GetActiveSession(name)?.Stop(true);
            }
            catch { /* ignore */ }
        }
    }

    // ------------------------------------------------------------ 图形会话
    public void StartPresentSession()
    {
        var s = new TraceEventSession(PresentSessionName) { StopOnDispose = true, BufferSizeMB = 8 };
        s.EnableProvider(DxgiGuid, TraceEventLevel.Verbose, 0x2,
            new TraceEventProviderOptions { EventIDsToEnable = new List<int> { 42, 55 } });
        s.EnableProvider(D3D9Guid, TraceEventLevel.Verbose, 0x2,
            new TraceEventProviderOptions { EventIDsToEnable = new List<int> { 1 } });
        // Vulkan / OpenGL 等非 DXGI 渲染的兜底：DxgKrnl 的 PresentHistory(Detailed)_Start
        s.EnableProvider(DxgKrnlGuid, TraceEventLevel.Verbose, 0x1,
            new TraceEventProviderOptions { EventIDsToEnable = new List<int> { 171, 215 } });

        var src = s.Source;
        src.UnhandledEvents += OnPresentEvent;
        _presentSession = s;
        new Thread(() => { try { src.Process(); } catch { } }) { IsBackground = true, Name = "etw-present" }.Start();
    }

    private void OnPresentEvent(TraceEvent e)
    {
        var g = e.ProviderGuid;
        int id = (int)e.ID;
        if (double.IsNaN(_presentOffset)) _presentOffset = Clock.ToMs(e.TimeStamp.ToUniversalTime()) - e.TimeStampRelativeMSec;
        double t = e.TimeStampRelativeMSec + _presentOffset;

        if (g == DxgiGuid && (id == 42 || id == 55))
        {
            if (e.EventDataLength < e.PointerSize + 4) return;
            ulong sc = ReadPtr(e);
            uint flags = (uint)Marshal.ReadInt32(e.DataStart, e.PointerSize);
            if ((flags & 0x1) != 0) return; // DXGI_PRESENT_TEST：不是真正的帧
            Present?.Invoke(e.ProcessID, sc, t);
        }
        else if (g == D3D9Guid && id == 1)
        {
            if (e.EventDataLength < e.PointerSize) return;
            Present?.Invoke(e.ProcessID, ReadPtr(e), t);
        }
        else if (g == DxgKrnlGuid && (id == 171 || id == 215))
        {
            Present?.Invoke(e.ProcessID, KernelSourceFlag | (uint)id, t);
        }
    }

    private static ulong ReadPtr(TraceEvent e) =>
        e.PointerSize == 8 ? (ulong)Marshal.ReadInt64(e.DataStart) : (uint)Marshal.ReadInt32(e.DataStart);

    // ------------------------------------------------------------ 内核会话（深度模式）
    public bool StartKernelSession()
    {
        lock (_kernelGate)
        {
            if (_kernelSession != null) return true;
            try
            {
                // 先用 EnumDeviceDrivers 填充驱动地址表，ETW 的映像事件会进一步补全
                if (_store.Drivers.Count == 0) _store.Drivers.AddBasesOnly(Platform.Native.LoadedDrivers());

                var s = new TraceEventSession(KernelSessionName) { StopOnDispose = true, BufferSizeMB = 16 };
                s.EnableKernelProvider(
                    KernelTraceEventParser.Keywords.Process |
                    KernelTraceEventParser.Keywords.Thread |
                    KernelTraceEventParser.Keywords.ImageLoad |
                    KernelTraceEventParser.Keywords.DeferedProcedureCalls |
                    KernelTraceEventParser.Keywords.Interrupt |
                    KernelTraceEventParser.Keywords.DiskIO);

                var src = s.Source;
                var k = src.Kernel;
                k.ImageLoad += OnImage;
                k.ImageDCStart += OnImage;
                k.PerfInfoDPC += d => _store.AddDpc(KT(d), d.ElapsedTimeMSec, _store.Drivers.Lookup(d.Routine), false, d.ProcessorNumber);
                k.PerfInfoTimerDPC += d => _store.AddDpc(KT(d), d.ElapsedTimeMSec, _store.Drivers.Lookup(d.Routine), false, d.ProcessorNumber);
                k.PerfInfoThreadedDPC += d => _store.AddDpc(KT(d), d.ElapsedTimeMSec, _store.Drivers.Lookup(d.Routine), false, d.ProcessorNumber);
                k.PerfInfoISR += d => _store.AddDpc(KT(d), d.ElapsedTimeMSec, _store.Drivers.Lookup(d.Routine), true, d.ProcessorNumber);
                k.DiskIORead += d => _store.Disk.Add(new DiskSample(KT(d), (float)d.ElapsedTimeMSec, d.ProcessID, d.TransferSize, false));
                k.DiskIOWrite += d => _store.Disk.Add(new DiskSample(KT(d), (float)d.ElapsedTimeMSec, d.ProcessID, d.TransferSize, true));

                _kernelSession = s;
                _kernelOffset = double.NaN;
                KernelError = null;
                new Thread(() => { try { src.Process(); } catch { } }) { IsBackground = true, Name = "etw-kernel" }.Start();
                return true;
            }
            catch (Exception ex)
            {
                KernelError = ex.Message;
                _kernelSession?.Dispose();
                _kernelSession = null;
                return false;
            }
        }
    }

    public void StopKernelSession()
    {
        lock (_kernelGate)
        {
            if (_kernelSession == null) return;
            try
            {
                KernelEventsLost += _kernelSession.EventsLost;
                _kernelSession.Dispose();
            }
            catch { }
            _kernelSession = null;
        }
    }

    private void OnImage(ImageLoadTraceData d) => _store.Drivers.Add(d.ImageBase, (ulong)d.ImageSize, d.FileName);

    private double KT(TraceEvent e)
    {
        if (double.IsNaN(_kernelOffset)) _kernelOffset = Clock.ToMs(e.TimeStamp.ToUniversalTime()) - e.TimeStampRelativeMSec;
        return e.TimeStampRelativeMSec + _kernelOffset;
    }

    public void Dispose()
    {
        StopKernelSession();
        try { _presentSession?.Dispose(); } catch { }
        _presentSession = null;
    }
}

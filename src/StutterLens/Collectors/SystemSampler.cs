using System.Diagnostics;
using StutterLens.Core;
using StutterLens.Platform;
using Microsoft.Win32;

namespace StutterLens.Collectors;

/// <summary>
/// 每秒一次的系统级采样：性能计数器（与任务管理器/性能监视器同源） + 全进程 CPU/缺页/IO 快照。
/// 另有 250ms 一次的前台窗口轮询（只读取前台窗口所属 PID，不打开进程）。
/// </summary>
public sealed class SystemSampler : IDisposable
{
    private readonly SignalStore _store;
    private Timer? _timer, _focusTimer;
    private int _busy;

    private PerformanceCounter? _cpuUtil, _cpuPerf, _dpc, _isr, _avail, _commit, _pagesIn, _diskLat, _diskIdle;
    private PerformanceCounter[] _cores = Array.Empty<PerformanceCounter>();
    private bool _gpuCountersOk = true;
    private readonly Dictionary<string, CounterSample> _gpuPrev = new();

    private Dictionary<(int, long), (long Cpu, long Faults, long Read)> _prev = new();
    private double _prevT = double.NaN;
    private HashSet<string> _prevNames = new(StringComparer.OrdinalIgnoreCase);
    private int _lastFg;
    private readonly int _selfPid = Environment.ProcessId;

    public volatile int TargetPid;

    // 手柄：每 15 秒刷新一次完整清单，每秒只查一次已知蓝牙手柄是否还连着
    private InputSnapshot _input = new();
    private int _inputTick;
    public InputSnapshot Input => _input;
    public double VramTotalMB { get; }
    public int LogicalCores { get; } = Environment.ProcessorCount;

    /// <summary>每次采样后回调（在采样线程上），参数为最新进程快照。</summary>
    public event Action<List<ProcInfo>, SysSample>? Sampled;

    // 自身开销统计
    private double _selfCpuSum; private int _selfCpuN; private double _selfCpuMax;
    public double SelfCpuAvg => _selfCpuN == 0 ? 0 : _selfCpuSum / _selfCpuN;
    public double SelfCpuMax => _selfCpuMax;
    public long SelfWorkingSetMB { get; private set; }

    public SystemSampler(SignalStore store)
    {
        _store = store;
        VramTotalMB = ReadVramTotalMB();
    }

    public void Start()
    {
        _cpuUtil = Pc("Processor Information", "% Processor Utility", "_Total");
        _cpuPerf = Pc("Processor Information", "% Processor Performance", "_Total");
        _dpc = Pc("Processor", "% DPC Time", "_Total");
        _isr = Pc("Processor", "% Interrupt Time", "_Total");
        _avail = Pc("Memory", "Available MBytes", null);
        _commit = Pc("Memory", "% Committed Bytes In Use", null);
        _pagesIn = Pc("Memory", "Pages Input/sec", null);
        _diskLat = Pc("PhysicalDisk", "Avg. Disk sec/Transfer", "_Total");
        _diskIdle = Pc("PhysicalDisk", "% Idle Time", "_Total");
        try
        {
            _cores = new PerformanceCounterCategory("Processor").GetInstanceNames()
                .Where(n => n != "_Total").Select(n => Pc("Processor", "% Processor Time", n)).Where(c => c != null).Select(c => c!).ToArray();
        }
        catch { }

        _timer = new Timer(_ => Tick(), null, 0, 1000);
        _focusTimer = new Timer(_ => PollFocus(), null, 0, 250);
    }

    private static PerformanceCounter? Pc(string cat, string counter, string? inst)
    {
        try
        {
            var c = inst == null ? new PerformanceCounter(cat, counter, true) : new PerformanceCounter(cat, counter, inst, true);
            c.NextValue();
            return c;
        }
        catch { return null; }
    }

    private static double V(PerformanceCounter? c)
    {
        try { return c?.NextValue() ?? double.NaN; } catch { return double.NaN; }
    }

    private void PollFocus()
    {
        int fg = Native.ForegroundPid();
        if (fg != _lastFg)
        {
            _lastFg = fg;
            _store.Focus.Add(new FocusEvent(Clock.NowMs, fg));
        }
    }

    public int ForegroundPid => _lastFg;

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            double now = Clock.NowMs;
            var snap = ProcessTable.Snapshot();
            var s = new SysSample { T = now };
            double dt = double.IsNaN(_prevT) ? 0 : now - _prevT;
            int target = TargetPid;
            var cur = new Dictionary<(int, long), (long, long, long)>(snap.Count);
            var top = new List<ProcCpu>();
            // 已经在运行的程序再开子进程（如浏览器开新标签页）不算“新程序启动”
            var prevNames = _prevNames;
            _prevNames = new HashSet<string>(snap.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var p in snap)
            {
                var key = (p.Pid, p.CreateTime);
                cur[key] = (p.CpuTime100ns, p.HardFaults, p.ReadBytes);
                if (dt <= 0) continue;
                if (_prev.TryGetValue(key, out var pv))
                {
                    double cpu = (p.CpuTime100ns - pv.Cpu) / (dt * 100.0); // 100 = 一个满核
                    if (p.Pid != 0 && cpu >= 5) top.Add(new ProcCpu(p.Pid, p.Name, cpu));
                    if (p.Pid == target)
                    {
                        s.TargetCpuPct = cpu;
                        s.TargetHardFaultsPerSec = (p.HardFaults - pv.Faults) * 1000.0 / dt;
                        s.TargetReadMBps = (p.ReadBytes - pv.Read) / 1048576.0 * 1000.0 / dt;
                    }
                    if (p.Pid == _selfPid)
                    {
                        double selfPctOfMachine = cpu / LogicalCores;
                        _selfCpuSum += selfPctOfMachine; _selfCpuN++;
                        _selfCpuMax = Math.Max(_selfCpuMax, selfPctOfMachine);
                        SelfWorkingSetMB = p.WorkingSet / 1048576;
                    }
                }
                else if (!double.IsNaN(_prevT) && !prevNames.Contains(p.Name))
                {
                    _store.ProcStarts.Add(new ProcStartEvent(now, p.Pid, p.ParentPid, p.Name));
                }
            }
            _prev = cur;
            _prevT = now;
            s.TopProcs = top.OrderByDescending(x => x.CpuPct).Take(8).ToList();

            s.CpuTotal = V(_cpuUtil);
            if (s.CpuTotal > 100) s.CpuTotal = 100;
            s.CpuPerf = V(_cpuPerf);
            s.DpcPct = V(_dpc);
            s.IsrPct = V(_isr);
            s.AvailMB = V(_avail);
            s.CommitPct = V(_commit);
            s.PagesInPerSec = V(_pagesIn);
            s.DiskLatencyMs = V(_diskLat) * 1000;
            s.DiskBusyPct = 100 - V(_diskIdle);
            double maxCore = 0;
            foreach (var c in _cores) maxCore = Math.Max(maxCore, V(c));
            s.CpuMaxCore = maxCore;
            SampleGpu(s, target, snap);
            s.VramTotalMB = VramTotalMB;
            s.BtPadConnected = PollInput(now);

            if (dt > 0)
            {
                _store.Sys.Add(s);
                Sampled?.Invoke(snap, s);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("sampler: " + ex.Message);
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private bool PollInput(double now)
    {
        try
        {
            var old = _input;
            bool first = _inputTick == 0;
            InputSnapshot cur;
            if (_inputTick++ % 15 == 0) cur = Peripherals.Snapshot();
            else
            {
                // 每秒只取 HID 设备列表比对，有变化才重新归并
                var ids = Peripherals.HidIds();
                cur = ids.SetEquals(old.HidIds) ? old : new InputSnapshot
                {
                    Devices = Peripherals.Devices(ids).Concat(old.Devices.Where(d => !d.HidId.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase))).ToList(),
                    HidIds = ids, Radio = old.Radio, WifiChannel = old.WifiChannel,
                };
            }
            if (!first && !ReferenceEquals(cur, old))
            {
                var oldKeys = old.Devices.Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var newKeys = cur.Devices.Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var d in old.Devices.Where(d => !newKeys.Contains(d.Key)).DistinctBy(d => d.Key))
                    _store.Input.Add(new InputEvent(now, d, false));
                foreach (var d in cur.Devices.Where(d => !oldKeys.Contains(d.Key)).DistinctBy(d => d.Key))
                    _store.Input.Add(new InputEvent(now, d, true));
            }
            _input = cur;
            return cur.BtPadConnected;
        }
        catch (Exception ex)
        {
            Log.Debug("input: " + ex.Message);
            return false;
        }
    }

    private void SampleGpu(SysSample s, int target, List<ProcInfo> snap)
    {
        if (!_gpuCountersOk) return;
        try
        {
            var data = new PerformanceCounterCategory("GPU Engine").ReadCategory();
            var util = data["utilization percentage"];
            var perEngine = new Dictionary<string, double>();
            var perPidEngine = new Dictionary<(int Pid, string Eng), double>();
            double targetMax = 0;
            var seen = new HashSet<string>();
            string targetPrefix = $"pid_{target}_";
            if (util != null)
            {
                foreach (InstanceData d in util.Values)
                {
                    var name = d.InstanceName;
                    seen.Add(name);
                    var smp = d.Sample;
                    if (_gpuPrev.TryGetValue(name, out var prev))
                    {
                        double v = CounterSample.Calculate(prev, smp);
                        int luid = name.IndexOf("_luid_", StringComparison.Ordinal);
                        int engType = name.IndexOf("_engtype_", StringComparison.Ordinal);
                        if (luid > 0 && engType > luid)
                        {
                            var engKey = name.Substring(luid, engType - luid);
                            perEngine[engKey] = perEngine.GetValueOrDefault(engKey) + v;
                            // 实例名形如 pid_1234_luid_..._eng_0_engtype_3D
                            if (name.StartsWith("pid_", StringComparison.Ordinal) && int.TryParse(name.AsSpan(4, luid - 4), out int gpid) && gpid != 0)
                                perPidEngine[(gpid, engKey)] = perPidEngine.GetValueOrDefault((gpid, engKey)) + v;
                            if (target != 0 && name.StartsWith(targetPrefix, StringComparison.Ordinal) && name.EndsWith("engtype_3D", StringComparison.Ordinal))
                                targetMax = Math.Max(targetMax, v);
                        }
                    }
                    _gpuPrev[name] = smp;
                }
            }
            foreach (var k in _gpuPrev.Keys.Where(k => !seen.Contains(k)).ToList()) _gpuPrev.Remove(k);
            s.GpuUtil = Math.Min(100, perEngine.Count == 0 ? 0 : perEngine.Values.Max());
            s.GpuTargetUtil = Math.Min(100, targetMax);
            var names = snap.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First().Name);
            s.GpuProcs = perPidEngine.GroupBy(kv => kv.Key.Pid)
                .Select(g => (Pid: g.Key, Util: Math.Min(100, g.Max(kv => kv.Value))))
                .Where(x => x.Util >= 5 && x.Pid != target)
                .OrderByDescending(x => x.Util).Take(5)
                .Select(x => new ProcCpu(x.Pid, names.GetValueOrDefault(x.Pid, "pid " + x.Pid), x.Util)).ToList();

            var mem = new PerformanceCounterCategory("GPU Adapter Memory").ReadCategory();
            var ded = mem["dedicated usage"];
            var shared = mem["shared usage"];
            double maxUsed = 0, maxShared = 0;
            if (ded != null)
                foreach (InstanceData d in ded.Values) maxUsed = Math.Max(maxUsed, d.RawValue);
            if (shared != null)
                foreach (InstanceData d in shared.Values) maxShared = Math.Max(maxShared, d.RawValue);
            s.VramUsedMB = maxUsed / 1048576.0;
            s.VramSharedMB = maxShared / 1048576.0;
        }
        catch (Exception ex)
        {
            _gpuCountersOk = false;
            Log.Debug("GPU 计数器不可用: " + ex.Message);
        }
    }

    /// <summary>从显卡驱动注册表读取最大独显显存。</summary>
    private static double ReadVramTotalMB()
    {
        double max = 0;
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return 0;
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (!sub.All(char.IsDigit)) continue;
                using var k = cls.OpenSubKey(sub);
                var v = k?.GetValue("HardwareInformation.qwMemorySize");
                double bytes = v switch
                {
                    long l => l,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                    int i => (uint)i,
                    _ => 0,
                };
                max = Math.Max(max, bytes);
            }
        }
        catch { }
        return max / 1048576.0;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _focusTimer?.Dispose();
        foreach (var c in new[] { _cpuUtil, _cpuPerf, _dpc, _isr, _avail, _commit, _pagesIn, _diskLat, _diskIdle }.Concat(_cores))
            c?.Dispose();
    }
}

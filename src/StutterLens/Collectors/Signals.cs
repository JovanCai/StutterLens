using StutterLens.Core;

namespace StutterLens.Collectors;

public readonly record struct DpcSample(double T, float DurMs, int Driver, bool Isr, int Cpu) : ITimed;
public readonly record struct DiskSample(double T, float LatencyMs, int Pid, int Bytes, bool Write) : ITimed;
public readonly record struct FocusEvent(double T, int Pid) : ITimed;
public readonly record struct ProcStartEvent(double T, int Pid, int ParentPid, string Name) : ITimed;

public sealed record ProcCpu(int Pid, string Name, double CpuPct /* 占用的“核”百分比，100 = 一个满核 */);

/// <summary>每秒一次的系统采样。</summary>
public sealed class SysSample : ITimed
{
    public double T { get; init; }
    public double CpuTotal;        // 全机 CPU 利用率 %
    public double CpuMaxCore;      // 最忙的逻辑核 %
    public double CpuPerf;         // % Processor Performance（频率相对基础频率，<100 表示降频）
    public double DpcPct, IsrPct;  // 全机 DPC / 中断时间占比
    public double AvailMB, CommitPct, PagesInPerSec;
    public double DiskLatencyMs, DiskBusyPct;
    public double GpuUtil;         // 最忙 GPU 引擎 %
    public double GpuTargetUtil;   // 目标游戏的 GPU 3D 引擎占用 %
    public double VramUsedMB, VramTotalMB;
    public double TargetCpuPct, TargetHardFaultsPerSec, TargetReadMBps;
    public List<ProcCpu> TopProcs = new();
}

/// <summary>DPC 例程地址 → 驱动名映射表。</summary>
public sealed class DriverTable
{
    private readonly object _gate = new();
    private readonly List<(ulong Base, ulong End, int Idx)> _ranges = new();
    private (ulong Base, ulong End, int Idx)[] _sorted = Array.Empty<(ulong, ulong, int)>();
    private readonly List<string> _names = new();
    private readonly Dictionary<string, int> _nameIdx = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _sorted.Length;

    public string Name(int idx) => idx >= 0 && idx < _names.Count ? _names[idx] : "未知驱动";

    public void Add(ulong imageBase, ulong size, string path)
    {
        if (imageBase < 0xFFFF800000000000UL) return; // 只关心内核地址空间
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return;
        lock (_gate)
        {
            if (!_nameIdx.TryGetValue(name, out int idx))
            {
                idx = _names.Count;
                _names.Add(name);
                _nameIdx[name] = idx;
            }
            _ranges.Add((imageBase, imageBase + Math.Max(size, 1), idx));
            _sorted = _ranges.OrderBy(r => r.Base).ToArray();
        }
    }

    /// <summary>仅有基址时（EnumDeviceDrivers），用下一个驱动的基址作为结束。</summary>
    public void AddBasesOnly(List<(ulong Base, string Name)> drivers)
    {
        var sorted = drivers.Where(d => d.Base >= 0xFFFF800000000000UL).OrderBy(d => d.Base).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            ulong end = i + 1 < sorted.Count ? sorted[i + 1].Base : sorted[i].Base + 0x1000000;
            Add(sorted[i].Base, end - sorted[i].Base, sorted[i].Name);
        }
    }

    public int Lookup(ulong addr)
    {
        var arr = _sorted;
        int lo = 0, hi = arr.Length - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (arr[mid].Base <= addr) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        // 区间可能重叠（两种来源），向前找第一个包含 addr 的
        for (int i = best; i >= 0 && i > best - 4; i--)
            if (addr < arr[i].End) return arr[i].Idx;
        return -1;
    }
}

/// <summary>所有时间序列信号的集中存放处。</summary>
public sealed class SignalStore
{
    public readonly TimedRing<DpcSample> Dpc = new(200_000);
    public readonly TimedRing<DiskSample> Disk = new(100_000);
    public readonly TimedRing<FocusEvent> Focus = new(2_000);
    public readonly TimedRing<ProcStartEvent> ProcStarts = new(2_000);
    public readonly TimedRing<SysSample> Sys = new(3_600);
    public readonly DriverTable Drivers = new();

    // 会话级 DPC/ISR 驱动累计（只在深度模式下有数据）
    private readonly object _dpcGate = new();
    private readonly Dictionary<int, (long Count, double TotalMs, double MaxMs)> _dpcTotals = new();

    /// <summary>只把“值得关注”的 DPC 存入环形缓冲（≥50µs），所有 DPC 都计入累计。</summary>
    public void AddDpc(double t, double durMs, int driver, bool isr, int cpu)
    {
        lock (_dpcGate)
        {
            _dpcTotals.TryGetValue(driver, out var v);
            _dpcTotals[driver] = (v.Count + 1, v.TotalMs + durMs, Math.Max(v.MaxMs, durMs));
        }
        if (durMs >= 0.05) Dpc.Add(new DpcSample(t, (float)durMs, driver, isr, cpu));
    }

    public List<(string Driver, long Count, double TotalMs, double MaxMs)> DpcTotalsSnapshot()
    {
        lock (_dpcGate)
            return _dpcTotals.Select(kv => (Drivers.Name(kv.Key), kv.Value.Count, kv.Value.TotalMs, kv.Value.MaxMs))
                .OrderByDescending(x => x.TotalMs).ToList();
    }

    public void ResetDpcTotals() { lock (_dpcGate) _dpcTotals.Clear(); }

    /// <summary>返回覆盖时刻 t 的系统采样（采样点 T 代表 [T-1s, T] 区间）。</summary>
    public SysSample? SampleAt(double t)
    {
        var list = Sys.Range(t - 100, t + 1600);
        return list.FirstOrDefault(s => s.T >= t) ?? list.LastOrDefault();
    }
}

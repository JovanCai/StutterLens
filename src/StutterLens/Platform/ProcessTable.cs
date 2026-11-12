using System.Runtime.InteropServices;

namespace StutterLens.Platform;

public sealed class ProcInfo
{
    public int Pid;
    public int ParentPid;
    public string Name = "";
    public long CreateTime;
    public long CpuTime100ns;     // 用户态 + 内核态
    public long HardFaults;        // 累计硬缺页次数
    public long WorkingSet;
    public long ReadBytes;         // 累计读取字节
}

/// <summary>
/// 通过 NtQuerySystemInformation(SystemProcessInformation) 一次性读取全部进程的
/// CPU 时间 / 硬缺页 / IO 统计。这是任务管理器使用的同一系统查询，
/// 不需要也不会打开任何进程句柄 —— 对反作弊保护的游戏进程完全无接触。
/// </summary>
public static class ProcessTable
{
    private static IntPtr _buf = IntPtr.Zero;
    private static int _len;
    private static readonly object Gate = new();
    private static readonly Dictionary<(int, long), string> NameCache = new();

    // SYSTEM_PROCESS_INFORMATION 在 x64 上的字段偏移
    private const int OffNext = 0, OffHardFault = 16, OffCreate = 32, OffUser = 40, OffKernel = 48,
        OffNameLen = 56, OffNameBuf = 64, OffPid = 80, OffParent = 88, OffWorkingSet = 144, OffReadBytes = 232;

    public static List<ProcInfo> Snapshot()
    {
        var list = new List<ProcInfo>(400);
        lock (Gate)
        {
            while (true)
            {
                if (_buf == IntPtr.Zero) { _len = 1 << 20; _buf = Marshal.AllocHGlobal(_len); }
                int st = Native.NtQuerySystemInformation(Native.SystemProcessInformation, _buf, _len, out int need);
                if (st == Native.STATUS_INFO_LENGTH_MISMATCH)
                {
                    Marshal.FreeHGlobal(_buf);
                    _len = Math.Max(need + 256 * 1024, _len * 2);
                    _buf = Marshal.AllocHGlobal(_len);
                    continue;
                }
                if (st != 0) return list;
                break;
            }

            var seen = new HashSet<(int, long)>();
            int offset = 0;
            while (true)
            {
                IntPtr p = _buf + offset;
                var pi = new ProcInfo
                {
                    HardFaults = (uint)Marshal.ReadInt32(p, OffHardFault),
                    CreateTime = Marshal.ReadInt64(p, OffCreate),
                    CpuTime100ns = Marshal.ReadInt64(p, OffUser) + Marshal.ReadInt64(p, OffKernel),
                    Pid = (int)Marshal.ReadIntPtr(p, OffPid).ToInt64(),
                    ParentPid = (int)Marshal.ReadIntPtr(p, OffParent).ToInt64(),
                    WorkingSet = Marshal.ReadInt64(p, OffWorkingSet),
                    ReadBytes = Marshal.ReadInt64(p, OffReadBytes),
                };
                var key = (pi.Pid, pi.CreateTime);
                seen.Add(key);
                if (!NameCache.TryGetValue(key, out var name))
                {
                    int nlen = (ushort)Marshal.ReadInt16(p, OffNameLen);
                    IntPtr nbuf = Marshal.ReadIntPtr(p, OffNameBuf);
                    name = pi.Pid == 0 ? "Idle" : (nbuf == IntPtr.Zero ? "System" : Marshal.PtrToStringUni(nbuf, nlen / 2));
                    NameCache[key] = name;
                }
                pi.Name = name;
                list.Add(pi);

                int next = Marshal.ReadInt32(p, OffNext);
                if (next == 0) break;
                offset += next;
            }

            // 清理已退出进程的名字缓存
            if (NameCache.Count > seen.Count + 200)
                foreach (var k in NameCache.Keys.Where(k => !seen.Contains(k)).ToList()) NameCache.Remove(k);
        }
        return list;
    }

    /// <summary>
    /// 通过 SystemProcessIdInformation 获取进程映像完整路径（同样无需进程句柄）。
    /// </summary>
    public static string? ImagePath(int pid)
    {
        const int cap = 2048;
        IntPtr nameBuf = Marshal.AllocHGlobal(cap);
        IntPtr info = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteIntPtr(info, 0, new IntPtr(pid));
            Marshal.WriteInt16(info, 8, 0);
            Marshal.WriteInt16(info, 10, unchecked((short)cap));
            Marshal.WriteInt32(info, 12, 0);
            Marshal.WriteIntPtr(info, 16, nameBuf);
            int st = Native.NtQuerySystemInformation(Native.SystemProcessIdInformation, info, 24, out _);
            if (st != 0) return null;
            int len = (ushort)Marshal.ReadInt16(info, 8);
            var nt = Marshal.PtrToStringUni(nameBuf, len / 2);
            return Native.NtPathToDos(nt);
        }
        catch { return null; }
        finally
        {
            Marshal.FreeHGlobal(nameBuf);
            Marshal.FreeHGlobal(info);
        }
    }
}

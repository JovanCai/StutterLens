using System.Runtime.InteropServices;
using System.Text;

namespace StutterLens.Platform;

/// <summary>
/// 所有 Win32/NT 调用集中在这里。
/// 反作弊安全约束：这里不存在任何 OpenProcess / ReadProcessMemory / 注入 / 钩子 调用，
/// 全部是系统级的只读查询，不会对游戏进程产生任何句柄。
/// </summary>
internal static class Native
{
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    public const int SystemProcessInformation = 5;
    public const int SystemProcessIdInformation = 88;

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool EnumDeviceDrivers([Out] IntPtr[]? imageBases, int cb, out int needed);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode)]
    public static extern int GetDeviceDriverBaseNameW(IntPtr imageBase, StringBuilder name, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern uint QueryDosDeviceW(string deviceName, StringBuilder target, int max);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    public const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("kernel32.dll")]
    public static extern uint GetConsoleProcessList(uint[] list, uint count);

    public delegate bool ConsoleCtrlHandler(int ctrlType);

    [DllImport("kernel32.dll")]
    public static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, bool add);

    /// <summary>控制台窗口只属于本进程（即用户双击 exe 打开的），退出时窗口会随之关闭。</summary>
    public static bool OwnsConsole()
    {
        try { return GetConsoleProcessList(new uint[4], 4) <= 1; } catch { return false; }
    }

    public static int ForegroundPid()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(h, out var pid);
        return (int)pid;
    }

    /// <summary>已加载的内核驱动列表（基址 + 文件名）。需管理员权限才能拿到真实地址。</summary>
    public static List<(ulong Base, string Name)> LoadedDrivers()
    {
        var result = new List<(ulong, string)>();
        try
        {
            EnumDeviceDrivers(null, 0, out int needed);
            if (needed <= 0) return result;
            var arr = new IntPtr[needed / IntPtr.Size];
            if (!EnumDeviceDrivers(arr, needed, out _)) return result;
            var sb = new StringBuilder(260);
            foreach (var b in arr)
            {
                sb.Clear();
                if (GetDeviceDriverBaseNameW(b, sb, sb.Capacity) > 0)
                    result.Add(((ulong)b.ToInt64(), sb.ToString()));
            }
        }
        catch { /* 忽略：权限不足时返回空 */ }
        return result;
    }

    private static Dictionary<string, string>? _dosMap;

    /// <summary>\Device\HarddiskVolume3\xx → D:\xx</summary>
    public static string NtPathToDos(string ntPath)
    {
        _dosMap ??= BuildDosMap();
        foreach (var (dev, letter) in _dosMap)
            if (ntPath.StartsWith(dev + "\\", StringComparison.OrdinalIgnoreCase))
                return letter + ntPath.Substring(dev.Length);
        return ntPath;
    }

    private static Dictionary<string, string> BuildDosMap()
    {
        var map = new Dictionary<string, string>();
        var sb = new StringBuilder(512);
        foreach (var d in DriveInfo.GetDrives())
        {
            var letter = d.Name.TrimEnd('\\');
            sb.Clear();
            if (QueryDosDeviceW(letter, sb, sb.Capacity) > 0) map[sb.ToString()] = letter;
        }
        return map;
    }
}

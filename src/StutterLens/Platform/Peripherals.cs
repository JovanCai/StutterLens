using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace StutterLens.Platform;

public enum InputKind { Gamepad, Mouse, Keyboard }
public enum Transport { Usb, Receiver, Bluetooth }

/// <summary>一个和游戏交互的输入设备（键盘、鼠标、手柄）。Key 是物理设备的实例 ID，同一设备的多个 HID 接口共用。</summary>
public sealed record InputDevice(string Name, InputKind Kind, Transport Transport, string Key, string HidId)
{
    public string KindName => Kind switch { InputKind.Gamepad => "手柄", InputKind.Mouse => "鼠标", _ => "键盘" };
    public string TransportName => Transport switch { Transport.Bluetooth => "蓝牙", Transport.Receiver => "无线接收器", _ => "有线" };
    public string Label => $"{Name}（{KindName}，{TransportName}）";
}

/// <summary>蓝牙适配器。LmpVersion 对应蓝牙规范版本：6=4.0，7=4.1，8=4.2，9=5.0……</summary>
public sealed record BtRadio(string Name, string InstanceId, int LmpVersion)
{
    public string Version => LmpVersion switch
    {
        <= 0 => "未知",
        < 6 => "4.0 以下",
        6 => "4.0", 7 => "4.1", 8 => "4.2", 9 => "5.0", 10 => "5.1", 11 => "5.2", 12 => "5.3", 13 => "5.4",
        _ => "5.4 以上",
    };
    public bool IsOld => LmpVersion is > 0 and < 9;
    /// <summary>CSR8510 (0A12:0001)：最常见的廉价蓝牙 4.0 适配器，仿制芯片很多。</summary>
    public bool IsCsrDongle => InstanceId.Contains("VID_0A12&PID_0001", StringComparison.OrdinalIgnoreCase);
}

public sealed class InputSnapshot
{
    /// <summary>此刻在线的键盘、鼠标、手柄。</summary>
    public List<InputDevice> Devices { get; init; } = new();
    /// <summary>此刻存在的 HID 设备实例 ID，用来比对设备接入和断开。</summary>
    public HashSet<string> HidIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public BtRadio? Radio { get; init; }
    /// <summary>当前连接的 Wi-Fi 信道，0 表示没有连接 Wi-Fi。1~14 为 2.4GHz 频段，和蓝牙同频。</summary>
    public int WifiChannel { get; init; }
    public bool Wifi24GHz => WifiChannel is >= 1 and <= 14;
    public bool BtPadConnected => Devices.Any(d => d.Kind == InputKind.Gamepad && d.Transport == Transport.Bluetooth);
}

/// <summary>
/// 外设清单：用 CfgMgr32（设备管理器同源的只读接口）读取键盘、鼠标、手柄和蓝牙适配器，用 WLAN API 读取 Wi-Fi 信道。
/// 只查询设备树，不打开任何设备，不读取按键或鼠标输入，也不涉及游戏进程。
/// </summary>
public static class Peripherals
{
    private const uint CR_SUCCESS = 0, CR_BUFFER_SMALL = 0x1A;
    private const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x1, CM_GETIDLIST_FILTER_PRESENT = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; public DEVPROPKEY(string g, uint p) { fmtid = new Guid(g); pid = p; } }

    private static readonly DEVPROPKEY PkName = new("b725f130-47ef-101a-a5f1-02608c9eebac", 10);
    private static readonly DEVPROPKEY PkHardwareIds = new("a45c254e-df1c-4efd-8020-67d146a850e0", 3);
    private static readonly DEVPROPKEY PkService = new("a45c254e-df1c-4efd-8020-67d146a850e0", 6);
    private static readonly DEVPROPKEY PkBusReportedDesc = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2", 4);
    private static readonly DEVPROPKEY PkContainerId = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);
    private static readonly DEVPROPKEY PkRadioLmpVersion = new("a92f26ca-eda7-4b1d-9db2-27b68aa5a2eb", 4);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint len, string? filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint devInst, string id, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint devInst, [Out] char[] buffer, uint len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, [Out] byte[]? buffer, ref uint size, uint flags);

    // 不走 HID 的 USB 手柄驱动（Xbox 有线手柄、Xbox 无线适配器）
    private static readonly string[] XinputServices = { "xusb22", "xboxgip", "dc1-controller" };
    private static readonly Regex ReceiverName = new("receiver|dongle|lightspeed|unifying|bolt|hyperspeed|nano|2\\.4|接收器", RegexOptions.IgnoreCase);

    // HID 实例 ID → 分类结果（null 表示不是键鼠手柄，比如灯效、音量键、厂商自定义接口）。
    // 设备拔掉后仍保留，断开事件需要用到名称。
    private static readonly ConcurrentDictionary<string, InputDevice?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>完整清单：输入设备 + 蓝牙适配器 + Wi-Fi 信道。几毫秒，适合每十几秒调用一次。</summary>
    public static InputSnapshot Snapshot()
    {
        try
        {
            var ids = HidIds();
            var devices = Devices(ids);
            foreach (var id in IdList("USB"))
            {
                if (!Locate(id, out var inst)) continue;
                var svc = StringProp(inst, PkService);
                if (svc != null && XinputServices.Contains(svc, StringComparer.OrdinalIgnoreCase))
                {
                    var name = StringProp(inst, PkBusReportedDesc) ?? StringProp(inst, PkName) ?? "Xbox 手柄";
                    bool adapter = name.Contains("adapter", StringComparison.OrdinalIgnoreCase) || name.Contains("适配器");
                    devices.Add(new InputDevice(name, InputKind.Gamepad, adapter ? Transport.Receiver : Transport.Usb, id, id));
                }
            }
            return new InputSnapshot { Devices = devices, HidIds = ids, Radio = FindRadio(), WifiChannel = Wlan.ConnectedChannel() };
        }
        catch
        {
            return new InputSnapshot();
        }
    }

    /// <summary>此刻存在的 HID 设备。只做设备树查询，开销很小，可以每秒调用。</summary>
    public static HashSet<string> HidIds() => new(IdList("HID"), StringComparer.OrdinalIgnoreCase);

    /// <summary>把 HID 接口归并成物理设备。一个设备通常有多个接口（如游戏鼠标带一个宏键盘接口），取第一个接口的类型。</summary>
    public static List<InputDevice> Devices(IEnumerable<string> hidIds)
    {
        var list = new List<InputDevice>();
        foreach (var g in hidIds.OrderBy(i => i, StringComparer.OrdinalIgnoreCase)
                     .Select(Classify).Where(d => d != null).Select(d => d!).GroupBy(d => d.Key))
        {
            var first = g.First();
            list.Add(first);
            // 接收器可能同时带着鼠标和键盘
            if (first.Transport == Transport.Receiver)
                list.AddRange(g.Where(d => d.Kind != first.Kind).GroupBy(d => d.Kind).Select(k => k.First()));
        }
        return list;
    }

    /// <summary>已经识别过的设备（包括已经拔掉的）。</summary>
    public static InputDevice? Known(string hidId) => Cache.TryGetValue(hidId, out var d) ? d : null;

    public static InputDevice? Classify(string hidId) => Cache.GetOrAdd(hidId, ClassifyUncached);

    private static InputDevice? ClassifyUncached(string hidId)
    {
        try
        {
            if (!Locate(hidId, out var inst)) return null;
            var hw = StringProp(inst, PkHardwareIds) ?? "";
            InputKind kind;
            if (hw.Contains("HID_DEVICE_SYSTEM_GAME", StringComparison.OrdinalIgnoreCase)) kind = InputKind.Gamepad;
            else if (hw.Contains("HID_DEVICE_SYSTEM_MOUSE", StringComparison.OrdinalIgnoreCase)) kind = InputKind.Mouse;
            else if (hw.Contains("HID_DEVICE_SYSTEM_KEYBOARD", StringComparison.OrdinalIgnoreCase)) kind = InputKind.Keyboard;
            else return null;

            // 经典蓝牙 HID 服务 {00001124}，低功耗蓝牙 HID 服务 {00001812}
            if (hidId.StartsWith(@"HID\{00001124-", StringComparison.OrdinalIgnoreCase)
                || hidId.StartsWith(@"HID\{00001812-", StringComparison.OrdinalIgnoreCase))
            {
                var container = StringProp(inst, PkContainerId);
                var name = (container != null ? BluetoothName(container) : null) ?? "蓝牙设备";
                return new InputDevice(name, kind, Transport.Bluetooth, container ?? hidId, hidId);
            }

            // USB：往上找到物理设备（去掉 &MI_xx 接口层）
            uint cur = inst;
            for (int i = 0; i < 4 && CM_Get_Parent(out var p, cur, 0) == CR_SUCCESS; i++)
            {
                cur = p;
                var id = DeviceId(cur) ?? "";
                if (!id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) || id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)) continue;
                var name = StringProp(cur, PkBusReportedDesc) ?? StringProp(cur, PkName) ?? id;
                return new InputDevice(name, kind, ReceiverName.IsMatch(name) ? Transport.Receiver : Transport.Usb, id, hidId);
            }
            // 虚拟设备（如外设软件创建的虚拟键盘）不算
            return null;
        }
        catch { return null; }
    }

    private static string? BluetoothName(string container)
    {
        foreach (var id in IdList("BTHENUM").Concat(IdList("BTHLE")))
        {
            if (!id.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase) && !id.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase)) continue;
            if (Locate(id, out var inst) && string.Equals(StringProp(inst, PkContainerId), container, StringComparison.OrdinalIgnoreCase))
                return StringProp(inst, PkName);
        }
        return null;
    }

    private static BtRadio? FindRadio()
    {
        // 经典蓝牙总线枚举器 BTH\MS_BTHBRB 的父设备就是蓝牙适配器
        var brb = IdList("BTH").FirstOrDefault(i => i.StartsWith(@"BTH\MS_BTHBRB", StringComparison.OrdinalIgnoreCase));
        if (brb == null || !Locate(brb, out var inst) || CM_Get_Parent(out var radio, inst, 0) != CR_SUCCESS) return null;
        var lmp = Prop(radio, PkRadioLmpVersion);
        return new BtRadio(StringProp(radio, PkName) ?? "蓝牙适配器", DeviceId(radio) ?? "", lmp is { Length: >= 1 } ? lmp[0] : 0);
    }

    private static List<string> IdList(string enumerator)
    {
        uint flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out var len, enumerator, flags) != CR_SUCCESS || len == 0) return new();
            var buf = new char[len];
            var cr = CM_Get_Device_ID_ListW(enumerator, buf, len, flags);
            if (cr == CR_BUFFER_SMALL) continue; // 设备列表在两次调用之间变化了
            if (cr != CR_SUCCESS) return new();
            return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        return new();
    }

    private static bool Locate(string id, out uint inst) => CM_Locate_DevNodeW(out inst, id, 0) == CR_SUCCESS;

    private static string? DeviceId(uint inst)
    {
        var buf = new char[512];
        return CM_Get_Device_IDW(inst, buf, (uint)buf.Length, 0) == CR_SUCCESS ? new string(buf).TrimEnd('\0') : null;
    }

    private static byte[]? Prop(uint inst, DEVPROPKEY key)
    {
        uint size = 0;
        var cr = CM_Get_DevNode_PropertyW(inst, ref key, out _, null, ref size, 0);
        if (cr != CR_BUFFER_SMALL || size == 0) return null;
        var buf = new byte[size];
        return CM_Get_DevNode_PropertyW(inst, ref key, out _, buf, ref size, 0) == CR_SUCCESS ? buf : null;
    }

    /// <summary>字符串、字符串列表和 GUID 属性统一转成文本（列表用 ; 连接）。</summary>
    private static string? StringProp(uint inst, DEVPROPKEY key)
    {
        uint size = 0;
        var cr = CM_Get_DevNode_PropertyW(inst, ref key, out var type, null, ref size, 0);
        if (cr != CR_BUFFER_SMALL || size == 0) return null;
        var buf = new byte[size];
        if (CM_Get_DevNode_PropertyW(inst, ref key, out type, buf, ref size, 0) != CR_SUCCESS) return null;
        const uint DEVPROP_TYPE_GUID = 0x0D;
        if (type == DEVPROP_TYPE_GUID && buf.Length >= 16) return new Guid(buf.AsSpan(0, 16)).ToString("B");
        var s = Encoding.Unicode.GetString(buf).TrimEnd('\0');
        return s.Length == 0 ? null : s.Replace('\0', ';');
    }

    private static class Wlan
    {
        [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint ver, IntPtr reserved, out uint negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out uint size, out IntPtr data, IntPtr valueType);
        [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr mem);

        private const int OpcodeChannelNumber = 8;
        private const int StateConnected = 1;
        private const int ItemSize = 16 + 512 + 4; // GUID + WCHAR[256] + WLAN_INTERFACE_STATE

        public static int ConnectedChannel()
        {
            IntPtr h = IntPtr.Zero, list = IntPtr.Zero;
            try
            {
                // 台式机通常没有无线网卡或没开 WLAN 服务，这时直接返回 0
                if (WlanOpenHandle(2, IntPtr.Zero, out _, out h) != 0) return 0;
                if (WlanEnumInterfaces(h, IntPtr.Zero, out list) != 0) return 0;
                int n = Marshal.ReadInt32(list);
                for (int i = 0; i < n; i++)
                {
                    IntPtr item = list + 8 + i * ItemSize;
                    if (Marshal.ReadInt32(item + 16 + 512) != StateConnected) continue;
                    var guid = Marshal.PtrToStructure<Guid>(item);
                    if (WlanQueryInterface(h, ref guid, OpcodeChannelNumber, IntPtr.Zero, out var size, out var data, IntPtr.Zero) != 0) continue;
                    int ch = size >= 4 ? Marshal.ReadInt32(data) : 0;
                    WlanFreeMemory(data);
                    if (ch > 0) return ch;
                }
                return 0;
            }
            catch { return 0; }
            finally
            {
                if (list != IntPtr.Zero) WlanFreeMemory(list);
                if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero);
            }
        }
    }
}

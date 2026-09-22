using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace BaronDeskAgent.ServiceCore.AntiTheft;

/// <param name="InstanceId">PnP instance id of the HID interface node.</param>
/// <param name="DeviceKey">Identifies the physical device; every interface of a composite device shares it.</param>
/// <param name="VendorProductId">e.g. <c>VID_046D&amp;PID_C077</c>: identifies the model, not the unit.</param>
internal sealed record UsbHidDevice(string InstanceId, string DeviceKey, string Name, string VendorProductId);

/// <summary>
/// Finds wired USB HID devices (keyboards, mice, headsets' HID interfaces) through SetupAPI.
/// Selected by device class, never by display name, so it works on any Windows language.
/// Bluetooth and non-USB HID devices are out of scope (they produce no reliable USB removal).
/// </summary>
internal static class UsbDeviceEnumerator
{
    private const uint DigcfPresent = 0x00000002;
    private const int ErrorNoMoreItems = 259;
    private const uint SpdrpDeviceDesc = 0x00000000;
    private const uint SpdrpFriendlyName = 0x0000000C;
    private const int MaxDeviceIdLength = 200;

    private static readonly Guid HidClassGuid = new("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
    private static readonly IntPtr InvalidHandle = new(-1);

    public static IReadOnlyList<UsbHidDevice> GetPresentWiredHidDevices()
    {
        var classGuid = HidClassGuid;
        var deviceSet = SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, DigcfPresent);
        if (deviceSet == InvalidHandle)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs failed.");
        }

        try
        {
            var devices = new List<UsbHidDevice>();
            for (uint index = 0; ; index++)
            {
                var data = NewDeviceInfoData();
                if (!SetupDiEnumDeviceInfo(deviceSet, index, ref data))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                    {
                        return devices;
                    }

                    throw new Win32Exception(error, "SetupDiEnumDeviceInfo failed.");
                }

                if (TryDescribe(deviceSet, ref data) is { } device)
                {
                    devices.Add(device);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceSet);
        }
    }

    /// <summary>Looks up one device by instance id; null unless it is a present, wired USB HID device.</summary>
    public static UsbHidDevice? TryGetWiredHidDevice(string instanceId)
    {
        var deviceSet = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (deviceSet == InvalidHandle)
        {
            return null;
        }

        try
        {
            var data = NewDeviceInfoData();
            if (!SetupDiOpenDeviceInfoW(deviceSet, instanceId, IntPtr.Zero, 0, ref data) || data.ClassGuid != HidClassGuid)
            {
                return null;
            }

            return TryDescribe(deviceSet, ref data);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceSet);
        }
    }

    public static bool IsUsbInstance(string instanceId) =>
        instanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase);

    private static UsbHidDevice? TryDescribe(IntPtr deviceSet, ref SpDevInfoData data)
    {
        var instanceId = GetInstanceId(deviceSet, ref data);
        if (instanceId is null || !IsUsbInstance(instanceId))
        {
            return null;
        }

        // Interfaces of a composite device (…&MI_00, …&MI_01) belong to one physical device: key by the parent.
        var deviceKey = instanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase)
            ? GetParentInstanceId(data.DevInst) ?? instanceId
            : instanceId;

        var name = GetProperty(deviceSet, ref data, SpdrpFriendlyName)
                   ?? GetProperty(deviceSet, ref data, SpdrpDeviceDesc)
                   ?? "Unknown device";

        return new UsbHidDevice(instanceId, deviceKey, name, ExtractVendorProductId(instanceId));
    }

    private static string ExtractVendorProductId(string instanceId)
    {
        // USB\VID_046D&PID_C077\... → VID_046D&PID_C077
        var parts = instanceId.Split('\\');
        return parts.Length > 1 ? parts[1].Split("&MI_", StringSplitOptions.None)[0] : string.Empty;
    }

    private static string? GetInstanceId(IntPtr deviceSet, ref SpDevInfoData data)
    {
        var buffer = new StringBuilder(MaxDeviceIdLength);
        return SetupDiGetDeviceInstanceIdW(deviceSet, ref data, buffer, buffer.Capacity, out _) ? buffer.ToString() : null;
    }

    private static string? GetParentInstanceId(uint devInst)
    {
        if (CM_Get_Parent(out var parent, devInst, 0) != 0)
        {
            return null;
        }

        var buffer = new char[MaxDeviceIdLength];
        return CM_Get_Device_IDW(parent, buffer, buffer.Length, 0) == 0
            ? new string(buffer).TrimEnd('\0')
            : null;
    }

    private static string? GetProperty(IntPtr deviceSet, ref SpDevInfoData data, uint property)
    {
        var buffer = new byte[512];
        if (!SetupDiGetDeviceRegistryPropertyW(deviceSet, ref data, property, out _, buffer, (uint)buffer.Length, out var size))
        {
            if (size == 0 || size > 64 * 1024)
            {
                return null;
            }

            buffer = new byte[size];
            if (!SetupDiGetDeviceRegistryPropertyW(deviceSet, ref data, property, out _, buffer, (uint)buffer.Length, out size))
            {
                return null;
            }
        }

        var value = Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static SpDevInfoData NewDeviceInfoData() => new() { cbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr parent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiOpenDeviceInfoW(IntPtr deviceSet, string instanceId, IntPtr parent, uint flags, ref SpDevInfoData data);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceSet, uint index, ref SpDevInfoData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceSet, ref SpDevInfoData data, StringBuilder instanceId, int size, out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr deviceSet,
        ref SpDevInfoData data,
        uint property,
        out uint registryDataType,
        byte[] buffer,
        uint bufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceSet);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint devInst, char[] buffer, int bufferLength, uint flags);
}

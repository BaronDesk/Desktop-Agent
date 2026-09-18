using System.Runtime.InteropServices;
using System.Text;

namespace BaronDeskAgent.ServiceCore.Hardware;

public static class WindowsDeviceEnumerator
{
    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_ALLCLASSES = 0x00000004;

    private const uint SPDRP_DEVICEDESC = 0x00000000;
    private const uint SPDRP_FRIENDLYNAME = 0x0000000C;

    private static readonly IntPtr InvalidHandleValue =
        new(-1);

    public static IReadOnlyList<WindowsDeviceInfo>
        GetPresentUsbDevices()
    {
        var result =
            new List<WindowsDeviceInfo>();

        IntPtr deviceInfoSet =
            SetupDiGetClassDevsW(
                IntPtr.Zero,
                null,
                IntPtr.Zero,
                DIGCF_PRESENT |
                DIGCF_ALLCLASSES);

        if (deviceInfoSet == InvalidHandleValue)
        {
            throw new InvalidOperationException(
                $"SetupDiGetClassDevs failed. Win32 error: {Marshal.GetLastWin32Error()}");
        }

        try
        {
            uint index = 0;

            while (true)
            {
                var deviceInfo =
                    new SP_DEVINFO_DATA
                    {
                        cbSize =
                            (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
                    };

                if (!SetupDiEnumDeviceInfo(
                        deviceInfoSet,
                        index,
                        ref deviceInfo))
                {
                    int error =
                        Marshal.GetLastWin32Error();

                    if (error == 259)
                    {
                        break;
                    }

                    throw new InvalidOperationException(
                        $"SetupDiEnumDeviceInfo failed. Win32 error: {error}");
                }

                index++;

                string? instanceId =
                    GetInstanceId(
                        deviceInfoSet,
                        ref deviceInfo);

                if (string.IsNullOrWhiteSpace(instanceId))
                {
                    continue;
                }

                if (!IsUsbInstance(instanceId))
                {
                    continue;
                }

                result.Add(
                    CreateDeviceInfo(
                        deviceInfoSet,
                        ref deviceInfo,
                        instanceId));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(
                deviceInfoSet);
        }

        return result;
    }

    public static WindowsDeviceInfo?
        GetDeviceInfo(string instanceId)
    {
        if (!IsUsbInstance(instanceId))
        {
            return null;
        }

        IntPtr deviceInfoSet =
            SetupDiGetClassDevsW(
                IntPtr.Zero,
                null,
                IntPtr.Zero,
                DIGCF_PRESENT |
                DIGCF_ALLCLASSES);

        if (deviceInfoSet == InvalidHandleValue)
        {
            return null;
        }

        try
        {
            uint index = 0;

            while (true)
            {
                var deviceInfo =
                    new SP_DEVINFO_DATA
                    {
                        cbSize =
                            (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
                    };

                if (!SetupDiEnumDeviceInfo(
                        deviceInfoSet,
                        index,
                        ref deviceInfo))
                {
                    break;
                }

                index++;

                string? currentInstanceId =
                    GetInstanceId(
                        deviceInfoSet,
                        ref deviceInfo);

                if (!string.Equals(
                        currentInstanceId,
                        instanceId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return CreateDeviceInfo(
                    deviceInfoSet,
                    ref deviceInfo,
                    currentInstanceId);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(
                deviceInfoSet);
        }

        return null;
    }

    public static string GetCanonicalDeviceKey(
        string instanceId)
    {
        string key = instanceId;

        if (instanceId.StartsWith(
                "USB\\",
                StringComparison.OrdinalIgnoreCase))
        {
            int miIndex =
                instanceId.IndexOf(
                    "&MI_",
                    StringComparison.OrdinalIgnoreCase);

            if (miIndex >= 0)
            {
                key = instanceId[..miIndex];
            }
        }

        return key;
    }

    private static WindowsDeviceInfo CreateDeviceInfo(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfo,
        string instanceId)
    {
        return new WindowsDeviceInfo
        {
            InstanceId = instanceId,
            DeviceName = GetDeviceName(
                deviceInfoSet,
                ref deviceInfo),
            ProductId = ExtractProductId(
                instanceId)
        };
    }

    private static bool IsUsbInstance(
        string instanceId)
    {
        return
            instanceId.StartsWith(
                "USB\\",
                StringComparison.OrdinalIgnoreCase)
            ||
            instanceId.StartsWith(
                "USBSTOR\\",
                StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetInstanceId(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfo)
    {
        var builder =
            new StringBuilder(512);

        if (!SetupDiGetDeviceInstanceIdW(
                deviceInfoSet,
                ref deviceInfo,
                builder,
                builder.Capacity,
                out _))
        {
            return null;
        }

        return builder.ToString();
    }

    private static string GetDeviceName(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfo)
    {
        string? friendlyName =
            GetRegistryProperty(
                deviceInfoSet,
                ref deviceInfo,
                SPDRP_FRIENDLYNAME);

        if (!string.IsNullOrWhiteSpace(
                friendlyName))
        {
            return friendlyName;
        }

        string? description =
            GetRegistryProperty(
                deviceInfoSet,
                ref deviceInfo,
                SPDRP_DEVICEDESC);

        if (!string.IsNullOrWhiteSpace(
                description))
        {
            return description;
        }

        return "Unknown device";
    }

    private static string? GetRegistryProperty(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfo,
        uint property)
    {
        byte[] buffer =
            new byte[4096];

        if (!SetupDiGetDeviceRegistryPropertyW(
                deviceInfoSet,
                ref deviceInfo,
                property,
                out _,
                buffer,
                (uint)buffer.Length,
                out uint requiredSize))
        {
            if (requiredSize == 0)
            {
                return null;
            }

            buffer =
                new byte[requiredSize];

            if (!SetupDiGetDeviceRegistryPropertyW(
                    deviceInfoSet,
                    ref deviceInfo,
                    property,
                    out _,
                    buffer,
                    (uint)buffer.Length,
                    out requiredSize))
            {
                return null;
            }
        }

        if (requiredSize == 0)
        {
            return null;
        }

        return Encoding.Unicode
            .GetString(
                buffer,
                0,
                (int)requiredSize)
            .TrimEnd('\0');
    }

    private static string ExtractProductId(
        string instanceId)
    {
        int pidIndex =
            instanceId.IndexOf(
                "PID_",
                StringComparison.OrdinalIgnoreCase);

        if (pidIndex < 0)
        {
            return string.Empty;
        }

        int start =
            pidIndex + 4;

        if (instanceId.Length <
            start + 4)
        {
            return string.Empty;
        }

        return instanceId.Substring(
            start,
            4);
    }

    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;

        public Guid ClassGuid;

        public uint DevInst;

        public IntPtr Reserved;
    }

    public sealed class WindowsDeviceInfo
    {
        public string InstanceId { get; init; } =
            string.Empty;

        public string DeviceName { get; init; } =
            string.Empty;

        public string ProductId { get; init; } =
            string.Empty;
    }

    [DllImport(
        "setupapi.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        EntryPoint = "SetupDiGetClassDevsW")]
    private static extern IntPtr SetupDiGetClassDevsW(
        IntPtr classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport(
        "setupapi.dll",
        SetLastError = true,
        EntryPoint = "SetupDiEnumDeviceInfo")]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport(
        "setupapi.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        EntryPoint = "SetupDiGetDeviceInstanceIdW")]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        StringBuilder deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport(
        "setupapi.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        byte[] propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport(
        "setupapi.dll",
        SetLastError = true,
        EntryPoint = "SetupDiDestroyDeviceInfoList")]
    private static extern bool SetupDiDestroyDeviceInfoList(
        IntPtr deviceInfoSet);
}
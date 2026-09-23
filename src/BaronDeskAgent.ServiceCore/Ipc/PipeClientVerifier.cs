using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using BaronDeskAgent.ServiceCore.Platform;

namespace BaronDeskAgent.ServiceCore.Ipc;

/// <summary>
/// Decides whether the process on the other end of the pipe may drive the lock screen.
/// </summary>
internal static class PipeClientVerifier
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public static bool IsTrusted(NamedPipeServerStream pipe, string? expectedExecutablePath, out string reason)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId))
        {
            reason = "could not identify the client process";
            return false;
        }

        // As a service, only a helper running in the active console session is accepted.
        if (InteractiveSession.IsServiceSession)
        {
            var console = InteractiveSession.GetActiveConsoleSessionId();
            if (console is null ||
                !InteractiveSession.TryGetProcessSessionId((int)processId, out var clientSession) ||
                clientSession != console)
            {
                reason = $"process {processId} is not in the active console session";
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedExecutablePath))
        {
            var imagePath = GetImagePath(processId);
            if (imagePath is null ||
                !string.Equals(Path.GetFullPath(imagePath), Path.GetFullPath(expectedExecutablePath), StringComparison.OrdinalIgnoreCase))
            {
                reason = $"process {processId} ({imagePath ?? "unknown image"}) is not the configured LockUI";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static string? GetImagePath(uint processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

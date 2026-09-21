using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BaronDeskAgent.ServiceCore.Services.Games;

/// <summary>
/// Launches processes into the interactive desktop user session (Session 1+).
/// When running in Session 0 as a Windows Service, uses Win32 CreateProcessAsUser.
/// When running in user session (development/testing) or if token access is unavailable,
/// falls back seamlessly to standard Process.Start.
/// </summary>
public sealed class InteractiveProcessLauncher
{
    private readonly ILogger<InteractiveProcessLauncher> _logger;

    public InteractiveProcessLauncher(ILogger<InteractiveProcessLauncher> logger)
    {
        _logger = logger;
    }

    public Process Launch(
        string executablePath,
        string? arguments = null,
        string? workingDirectory = null)
    {
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException($"Executable not found at path: {executablePath}", executablePath);
        }

        var workDir = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)
            ? workingDirectory
            : Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory;

        // Check if we are running in Session 0 (Windows Service)
        var currentSessionId = Process.GetCurrentProcess().SessionId;
        if (currentSessionId == 0 && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var process = LaunchAsActiveUser(executablePath, arguments, workDir);
                if (process is not null)
                {
                    _logger.LogInformation(
                        "Launched process via CreateProcessAsUser into active desktop session. Pid={Pid}, Path={Path}",
                        process.Id, executablePath);
                    return process;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "CreateProcessAsUser failed; falling back to standard Process.Start. Path={Path}",
                    executablePath);
            }
        }

        // Standard process launch (for interactive user session or fallback)
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments ?? string.Empty,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        var proc = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Process.Start returned null for executable: {executablePath}");

        _logger.LogInformation(
            "Launched process via standard Process.Start. Pid={Pid}, Path={Path}",
            proc.Id, executablePath);

        return proc;
    }

    private Process? LaunchAsActiveUser(string executablePath, string? arguments, string workingDirectory)
    {
        uint activeSessionId = WTSGetActiveConsoleSessionId();
        if (activeSessionId == 0xFFFFFFFF)
        {
            _logger.LogWarning("No active console session found (WTSGetActiveConsoleSessionId returned 0xFFFFFFFF).");
            return null;
        }

        if (!WTSQueryUserToken(activeSessionId, out var userToken))
        {
            var err = Marshal.GetLastWin32Error();
            _logger.LogWarning("WTSQueryUserToken failed for Session {SessionId} (Error={Error}).", activeSessionId, err);
            return null;
        }

        IntPtr duplicatedToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;

        try
        {
            var sa = new SECURITY_ATTRIBUTES();
            sa.nLength = Marshal.SizeOf(sa);

            if (!DuplicateTokenEx(
                    userToken,
                    TOKEN_ALL_ACCESS,
                    ref sa,
                    SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                    TOKEN_TYPE.TokenPrimary,
                    out duplicatedToken))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed.");
            }

            if (!CreateEnvironmentBlock(out environment, duplicatedToken, false))
            {
                _logger.LogWarning("CreateEnvironmentBlock failed (Error={Error}). Continuing with default env.", Marshal.GetLastWin32Error());
            }

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.lpDesktop = @"winsta0\default";

            var cmdLine = string.IsNullOrWhiteSpace(arguments)
                ? $"\"{executablePath}\""
                : $"\"{executablePath}\" {arguments}";

            var pi = new PROCESS_INFORMATION();
            uint creationFlags = NORMAL_PRIORITY_CLASS | CREATE_UNICODE_ENVIRONMENT;

            bool success = CreateProcessAsUser(
                duplicatedToken,
                null,
                cmdLine,
                ref sa,
                ref sa,
                false,
                creationFlags,
                environment != IntPtr.Zero ? environment : IntPtr.Zero,
                workingDirectory,
                ref si,
                out pi);

            if (!success)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed.");
            }

            try
            {
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);

                return Process.GetProcessById(pi.dwProcessId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to obtain managed Process object for Pid={Pid}.", pi.dwProcessId);
                return null;
            }
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (duplicatedToken != IntPtr.Zero) CloseHandle(duplicatedToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    #region Win32 P/Invoke

    private const uint TOKEN_ALL_ACCESS = 0x000F0000 | 0x001F;
    private const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    private enum SECURITY_IMPERSONATION_LEVEL
    {
        SecurityAnonymous,
        SecurityIdentification,
        SecurityImpersonation,
        SecurityDelegation
    }

    private enum TOKEN_TYPE
    {
        TokenPrimary = 1,
        TokenImpersonation
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        ref SECURITY_ATTRIBUTES lpTokenAttributes,
        SECURITY_IMPERSONATION_LEVEL impersonationLevel,
        TOKEN_TYPE tokenType,
        out IntPtr phNewToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        string lpCommandLine,
        ref SECURITY_ATTRIBUTES lpProcessAttributes,
        ref SECURITY_ATTRIBUTES lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    #endregion
}

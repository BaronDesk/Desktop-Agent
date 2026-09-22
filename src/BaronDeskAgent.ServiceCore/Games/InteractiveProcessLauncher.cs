using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BaronDeskAgent.ServiceCore.Platform;
using Microsoft.Win32.SafeHandles;

namespace BaronDeskAgent.ServiceCore.Games;

/// <summary>
/// Starts a program where the gamer can see it: in the console user's session when the agent runs as a
/// service (Session 0), or directly when the agent runs in a desktop session (development).
/// </summary>
public sealed class InteractiveProcessLauncher
{
    private const uint TokenQuery = 0x0008;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint TokenAdjustSessionId = 0x0100;
    private const uint PrimaryTokenAccess = TokenQuery | TokenDuplicate | TokenAssignPrimary | TokenAdjustDefault | TokenAdjustSessionId;

    private const uint NormalPriorityClass = 0x00000020;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    private readonly ILogger<InteractiveProcessLauncher> _logger;

    public InteractiveProcessLauncher(ILogger<InteractiveProcessLauncher> logger)
    {
        _logger = logger;
    }

    /// <exception cref="FileNotFoundException">The executable does not exist.</exception>
    /// <exception cref="InvalidOperationException">No user is signed in, or the process exited immediately.</exception>
    public Process Launch(string executablePath, string? arguments, string? workingDirectory)
    {
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("Executable not found.", executablePath);
        }

        var directory = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)
            ? workingDirectory
            : Path.GetDirectoryName(executablePath)!;

        // In Session 0 there is deliberately no fallback to Process.Start: that would run the program
        // invisibly, as LocalSystem.
        var process = InteractiveSession.IsServiceSession
            ? StartInConsoleUserSession(executablePath, arguments, directory)
            : StartInCurrentSession(executablePath, arguments, directory);

        _logger.LogInformation("Started {Path} (Pid={Pid}).", executablePath, process.Id);
        return process;
    }

    private static Process StartInCurrentSession(string executablePath, string? arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments ?? string.Empty,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Process.Start returned no process for {executablePath}.");
    }

    private static Process StartInConsoleUserSession(string executablePath, string? arguments, string workingDirectory)
    {
        using var userToken = InteractiveSession.TryGetConsoleUserToken()
            ?? throw new InvalidOperationException("No user is signed in on the console.");

        if (!DuplicateTokenEx(userToken, PrimaryTokenAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var primaryToken))
        {
            primaryToken.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed.");
        }

        using (primaryToken)
        {
            CreateEnvironmentBlock(out var environment, primaryToken, inherit: false);
            try
            {
                var startupInfo = new StartupInfo
                {
                    cb = Marshal.SizeOf<StartupInfo>(),
                    lpDesktop = @"winsta0\default"
                };

                // CreateProcessW may write to the command line buffer, so it must be mutable.
                var commandLine = new StringBuilder(string.IsNullOrWhiteSpace(arguments)
                    ? $"\"{executablePath}\""
                    : $"\"{executablePath}\" {arguments}");

                if (!CreateProcessAsUser(
                        primaryToken,
                        null,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        inheritHandles: false,
                        NormalPriorityClass | CreateUnicodeEnvironment,
                        environment,
                        workingDirectory,
                        ref startupInfo,
                        out var processInfo))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed.");
                }

                try
                {
                    // Resolved while hProcess is still open, so the PID cannot be recycled in between.
                    return Process.GetProcessById(processInfo.dwProcessId);
                }
                catch (ArgumentException)
                {
                    throw new InvalidOperationException($"{executablePath} exited immediately after starting.");
                }
                finally
                {
                    CloseHandle(processInfo.hThread);
                    CloseHandle(processInfo.hProcess);
                }
            }
            finally
            {
                if (environment != IntPtr.Zero)
                {
                    DestroyEnvironmentBlock(environment);
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
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
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        SafeAccessTokenHandle existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out SafeAccessTokenHandle newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessAsUserW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        SafeAccessTokenHandle token,
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

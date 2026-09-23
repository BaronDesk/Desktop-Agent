using System.Security.Principal;
using System.Text;
using BaronDeskAgent.ServiceCore.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace BaronDeskAgent.ServiceCore.Credentials;

/// <summary>
/// Provisioning commands, run from an elevated prompt before the service starts:
/// <code>
/// BaronDeskAgent.ServiceCore.exe --set-station-token     (token read from stdin or typed hidden)
/// BaronDeskAgent.ServiceCore.exe --clear-station-token
/// </code>
/// The token is never accepted as a command-line argument: arguments end up in shell history and are visible
/// to other processes.
/// </summary>
internal static class CredentialCommandLine
{
    private const string SetCommand = "--set-station-token";
    private const string ClearCommand = "--clear-station-token";

    /// <returns>True when <paramref name="args"/> was a credential command (the host must not start).</returns>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || (args[0] != SetCommand && args[0] != ClearCommand))
        {
            return false;
        }

        if (args.Length > 1)
        {
            Console.Error.WriteLine($"{args[0]} takes no arguments. Pipe the token on stdin or type it when asked.");
            exitCode = 2;
            return true;
        }

        if (!IsElevated())
        {
            Console.Error.WriteLine("Run this command from an elevated (administrator) prompt.");
            exitCode = 1;
            return true;
        }

        var store = new DpapiStationCredentialStore(NullLogger<DpapiStationCredentialStore>.Instance);
        exitCode = args[0] == SetCommand ? SetToken(store) : ClearToken(store);
        return true;
    }

    private static int SetToken(DpapiStationCredentialStore store)
    {
        var token = Console.IsInputRedirected ? Console.In.ReadLine()?.Trim() : ReadHidden("Station token: ");

        try
        {
            store.SaveToken(token ?? string.Empty);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        Console.WriteLine($"Station credential stored in {AgentPaths.CredentialFile} (DPAPI, machine scope).");
        return 0;
    }

    private static int ClearToken(DpapiStationCredentialStore store)
    {
        Console.WriteLine(store.DeleteToken() ? "Station credential removed." : "No station credential was stored.");
        return 0;
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var token = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return token.ToString().Trim();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (token.Length > 0)
                {
                    token.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                token.Append(key.KeyChar);
            }
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

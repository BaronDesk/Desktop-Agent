using System.Security.Principal;
using System.Text;
using BaronDeskAgent.ServiceCore.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace BaronDeskAgent.ServiceCore.Credentials;

/// <summary>
/// Provisioning commands, run from an elevated prompt before the service starts:
/// <code>
/// BaronDeskAgent.ServiceCore.exe --set-enrollment-token  (one-time token from the dashboard; the service enrolls on start)
/// BaronDeskAgent.ServiceCore.exe --clear-enrollment-token
/// BaronDeskAgent.ServiceCore.exe --set-station-token     (manual fallback: a station JWT issued out of band)
/// BaronDeskAgent.ServiceCore.exe --clear-station-token   (forgets the station identity: credential and key pair)
/// </code>
/// Tokens are never accepted as command-line arguments: arguments end up in shell history and are visible
/// to other processes. They are read from stdin or typed hidden.
/// </summary>
internal static class CredentialCommandLine
{
    private const string SetEnrollmentCommand = "--set-enrollment-token";
    private const string ClearEnrollmentCommand = "--clear-enrollment-token";
    private const string SetStationCommand = "--set-station-token";
    private const string ClearStationCommand = "--clear-station-token";

    private static readonly string[] Commands = [SetEnrollmentCommand, ClearEnrollmentCommand, SetStationCommand, ClearStationCommand];

    /// <returns>True when <paramref name="args"/> was a credential command (the host must not start).</returns>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || !Commands.Contains(args[0]))
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

        var station = new DpapiStationCredentialStore(NullLogger<DpapiStationCredentialStore>.Instance);
        var enrollment = new DpapiEnrollmentTokenStore(NullLogger<DpapiEnrollmentTokenStore>.Instance);

        exitCode = args[0] switch
        {
            SetEnrollmentCommand => SetEnrollmentToken(enrollment, station),
            ClearEnrollmentCommand => Report(enrollment.DeleteToken(), "Enrollment token removed.", "No enrollment token was stored."),
            SetStationCommand => SetToken(station, "Station token: ", $"Station credential stored in {AgentPaths.CredentialFile} (DPAPI, machine scope)."),
            _ => ClearStationIdentity(station)
        };
        return true;
    }

    private static int SetEnrollmentToken(DpapiEnrollmentTokenStore enrollment, DpapiStationCredentialStore station)
    {
        var exitCode = SetToken(
            enrollment,
            "Enrollment token: ",
            $"Enrollment token stored in {AgentPaths.EnrollmentTokenFile} (DPAPI, machine scope). " +
            "Start the service: it enrolls on its first connection, then an admin approves the station in the dashboard.");

        if (exitCode == 0 && station.TryGetToken() is not null)
        {
            Console.WriteLine($"Note: this station already has a station credential, so it will not re-enroll. Run {ClearStationCommand} first to re-enroll.");
        }

        return exitCode;
    }

    private static int SetToken(DpapiTokenStore store, string prompt, string successMessage)
    {
        var token = Console.IsInputRedirected ? Console.In.ReadLine()?.Trim() : ReadHidden(prompt);

        try
        {
            store.SaveToken(token ?? string.Empty);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        Console.WriteLine(successMessage);
        return 0;
    }

    private static int ClearStationIdentity(DpapiStationCredentialStore station)
    {
        var keyRemoved = new DpapiStationKeyStore(NullLogger<DpapiStationKeyStore>.Instance).Delete();
        return Report(station.DeleteToken() | keyRemoved, "Station credential and key pair removed.", "No station credential was stored.");
    }

    private static int Report(bool removed, string removedMessage, string absentMessage)
    {
        Console.WriteLine(removed ? removedMessage : absentMessage);
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

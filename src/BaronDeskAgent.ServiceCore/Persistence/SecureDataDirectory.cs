using System.Security.AccessControl;
using System.Security.Principal;

namespace BaronDeskAgent.ServiceCore.Persistence;

/// <summary>
/// Locks the agent's data directory down to SYSTEM and Administrators.
/// </summary>
/// <remarks>
/// <c>C:\ProgramData</c> lets standard users create files and folders. Without this, a gamer could
/// pre-create the folder or the SQLite <c>-wal</c>/<c>-shm</c> files and tamper with the game catalog
/// that the service launches programs from. Only applied when running as LocalSystem, so a developer
/// running the agent from the IDE keeps access to their own files.
/// </remarks>
internal static class SecureDataDirectory
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static void Prepare(string rootDirectory, string dataDirectory, ILogger logger)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem)
        {
            Directory.CreateDirectory(dataDirectory);
            return;
        }

        var security = CreateSecurity();
        foreach (var directory in new[] { rootDirectory, dataDirectory })
        {
            var info = new DirectoryInfo(directory);
            if (info.Exists)
            {
                info.SetAccessControl(security);
            }
            else
            {
                security.CreateDirectory(directory);
            }
        }

        RemoveFilesNotOwnedByTrustedPrincipals(dataDirectory, logger);
    }

    private static DirectorySecurity CreateSecurity()
    {
        var security = new DirectorySecurity();
        security.SetOwner(LocalSystem);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var sid in new[] { LocalSystem, Administrators })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    private static void RemoveFilesNotOwnedByTrustedPrincipals(string dataDirectory, ILogger logger)
    {
        foreach (var file in new DirectoryInfo(dataDirectory).EnumerateFiles())
        {
            var owner = file.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (owner == LocalSystem || owner == Administrators)
            {
                continue;
            }

            logger.LogCritical(
                "Deleting {File}: it was created by {Owner}, not by the agent. Possible tampering attempt.",
                file.FullName,
                owner);

            file.Delete();
        }
    }
}

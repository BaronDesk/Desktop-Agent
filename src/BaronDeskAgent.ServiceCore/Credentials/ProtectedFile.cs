using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace BaronDeskAgent.ServiceCore.Credentials;

/// <summary>
/// A small secret kept on disk encrypted with DPAPI (machine scope), in a file only SYSTEM and Administrators can read.
/// Shared by the station credential, the one-time enrollment token and the station key.
/// </summary>
/// <remarks>
/// <see cref="DataProtectionScope.LocalMachine"/> is required because the service must decrypt with nobody signed in.
/// Machine scope means any process on this PC could decrypt the blob, so the real protection is the file ACL.
/// A blob copied to another PC (e.g. a cloned disk image) cannot be decrypted there.
/// </remarks>
internal static class ProtectedFile
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <returns>The decrypted bytes (zero them after use), or null when the file does not exist.</returns>
    /// <exception cref="CryptographicException">Corrupted, protected for another purpose, or encrypted on another machine.</exception>
    /// <exception cref="IOException">The file exists but cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file exists but cannot be read.</exception>
    public static byte[]? Read(string path, byte[] entropy)
    {
        byte[] protectedBytes;
        try
        {
            protectedBytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        return ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.LocalMachine);
    }

    public static void Write(string path, byte[] entropy, byte[] plaintext)
    {
        var protectedBytes = ProtectedData.Protect(plaintext, entropy, DataProtectionScope.LocalMachine);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Written to a temp file created with its final ACL, then moved into place: readers never see a partial
        // file, and the secret is never readable by anyone else, not even for a moment.
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileInfo(temporaryPath).Create(
                       FileMode.CreateNew,
                       FileSystemRights.Write | FileSystemRights.ReadData,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough,
                       CreateFileSecurity()))
            {
                stream.Write(protectedBytes);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <returns>True when a file was removed.</returns>
    public static bool Delete(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private static FileSecurity CreateFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, AccessControlType.Allow));

        using var identity = WindowsIdentity.GetCurrent();
        if (identity.IsSystem)
        {
            security.SetOwner(LocalSystem);
        }
        else if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            // Elevated provisioning: owned by Administrators so the service's startup ownership check keeps the file.
            security.SetOwner(Administrators);
        }
        else
        {
            // Only non-elevated test/development processes get here; they must be able to read their own file.
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }
}

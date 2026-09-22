using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using BaronDeskAgent.ServiceCore.Persistence;

namespace BaronDeskAgent.ServiceCore.Credentials;

/// <summary>
/// Keeps the station credential encrypted with DPAPI, machine scope, in a file only SYSTEM and Administrators can read.
/// </summary>
/// <remarks>
/// <see cref="DataProtectionScope.LocalMachine"/> is required because the service must decrypt with nobody signed in.
/// Machine scope means any process on this PC could decrypt the blob, so the real protection is the file ACL.
/// A blob copied to another PC (e.g. a cloned disk image) cannot be decrypted there and is treated as absent.
/// Honest limit: an administrator or SYSTEM attacker on the station can extract the credential; the blast radius
/// is one station, and the backend can revoke and rotate it.
/// </remarks>
public sealed class DpapiStationCredentialStore : IStationCredentialStore
{
    internal const int MaxTokenLength = 8192;

    // Not a secret: binds the blob to this purpose so another app's machine-scope DPAPI data cannot stand in for it.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BaronDesk.StationCredential.v1");

    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private readonly string _path;
    private readonly ILogger<DpapiStationCredentialStore> _logger;

    public DpapiStationCredentialStore(ILogger<DpapiStationCredentialStore> logger)
        : this(AgentPaths.CredentialFile, logger)
    {
    }

    internal DpapiStationCredentialStore(string path, ILogger<DpapiStationCredentialStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public string? TryGetToken()
    {
        byte[] protectedBytes;
        try
        {
            protectedBytes = File.ReadAllBytes(_path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogError("The station credential file cannot be read ({Error}).", ex.GetType().Name);
            return null;
        }

        try
        {
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            try
            {
                var token = Encoding.UTF8.GetString(bytes);
                if (IsValidToken(token))
                {
                    return token;
                }

                _logger.LogError("The stored station credential is malformed; re-provision it.");
                return null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (CryptographicException)
        {
            // Corrupted, or encrypted on another machine (cloned image). Never log the blob.
            _logger.LogError("The station credential cannot be decrypted on this machine; re-provision it.");
            return null;
        }
    }

    public void SaveToken(string token)
    {
        if (!IsValidToken(token))
        {
            throw new ArgumentException(
                $"The token must be 1-{MaxTokenLength} printable ASCII characters without spaces.",
                nameof(token));
        }

        var bytes = Encoding.UTF8.GetBytes(token);
        byte[] protectedBytes;
        try
        {
            protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        // Written to a temp file created with its final ACL, then moved into place: readers never see a partial
        // file, and the credential is never readable by anyone else, not even for a moment.
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
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

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        _logger.LogInformation("Station credential stored (DPAPI, machine scope).");
    }

    public bool DeleteToken()
    {
        if (!File.Exists(_path))
        {
            return false;
        }

        File.Delete(_path);
        _logger.LogInformation("Station credential removed.");
        return true;
    }

    /// <summary>JWTs and opaque tokens are printable ASCII; anything else could corrupt the Authorization header.</summary>
    internal static bool IsValidToken(string? token) =>
        !string.IsNullOrEmpty(token) &&
        token.Length <= MaxTokenLength &&
        token.All(c => c is > ' ' and <= '~');

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

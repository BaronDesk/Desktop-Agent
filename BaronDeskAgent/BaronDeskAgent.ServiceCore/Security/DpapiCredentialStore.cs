using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using BaronDeskAgent.ServiceCore.Configuration;
using Microsoft.Extensions.Options;

namespace BaronDeskAgent.ServiceCore.Security;

/// <summary>
/// DPAPI-backed credential store using DataProtectionScope.LocalMachine.
/// The JWT is encrypted to the local machine key, allowing any process on the same
/// machine (including the Windows Service in Session 0) to decrypt it.
///
/// Storage location: %ProgramData%\BaronDesk\station.dat (configurable via AgentOptions.StationDataPath).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialStore : IStationCredentialStore
{
    private const string DefaultRelativePath = @"BaronDesk\station.dat";

    /// <summary>
    /// Optional per-install entropy derived from the machine serial number.
    /// Adds an additional layer beyond the DPAPI machine key.
    /// </summary>
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("BaronDesk.Station.Credential.v1");

    private readonly string _filePath;
    private readonly ILogger<DpapiCredentialStore> _logger;

    public DpapiCredentialStore(
        IOptions<AgentOptions> options,
        ILogger<DpapiCredentialStore> logger)
    {
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(options.Value.StationDataPath))
        {
            _filePath = options.Value.StationDataPath;
        }
        else
        {
            var programData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            _filePath = Path.Combine(programData, DefaultRelativePath);
        }
    }

    public bool HasToken
    {
        get
        {
            try
            {
                return File.Exists(_filePath) && new FileInfo(_filePath).Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task<string?> LoadTokenAsync()
    {
        if (!File.Exists(_filePath))
        {
            _logger.LogDebug("No credential file found at {Path}.", _filePath);
            return null;
        }

        try
        {
            var encryptedBytes = await File.ReadAllBytesAsync(_filePath);

            var decryptedBytes = ProtectedData.Unprotect(
                encryptedBytes,
                Entropy,
                DataProtectionScope.LocalMachine);

            var jwt = Encoding.UTF8.GetString(decryptedBytes);

            _logger.LogInformation(
                "Station JWT loaded from DPAPI credential store. Path={Path}, TokenLength={Length}",
                _filePath, jwt.Length);

            return jwt;
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex,
                "Failed to decrypt station credential. The file may be corrupted or was encrypted on a different machine. Path={Path}",
                _filePath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load station credential from {Path}.", _filePath);
            return null;
        }
    }

    public async Task SaveTokenAsync(string jwt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jwt);

        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(jwt);

            var encryptedBytes = ProtectedData.Protect(
                plainBytes,
                Entropy,
                DataProtectionScope.LocalMachine);

            // Ensure directory exists
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write atomically: write to temp file then move
            var tempPath = _filePath + ".tmp";

            await using (var fs = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                await fs.WriteAsync(encryptedBytes);
                await fs.FlushAsync();
            }

            File.Move(tempPath, _filePath, overwrite: true);

            _logger.LogInformation(
                "Station JWT saved to DPAPI credential store. Path={Path}, EncryptedSize={Size} bytes",
                _filePath, encryptedBytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save station credential to {Path}.", _filePath);
            throw;
        }
    }

    public Task ClearTokenAsync()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
                _logger.LogInformation("Station credential cleared from {Path}.", _filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear station credential from {Path}.", _filePath);
        }

        return Task.CompletedTask;
    }
}

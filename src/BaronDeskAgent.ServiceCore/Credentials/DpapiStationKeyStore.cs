using System.Security.Cryptography;
using System.Text;
using BaronDeskAgent.ServiceCore.Persistence;

namespace BaronDeskAgent.ServiceCore.Credentials;

/// <summary>
/// Keeps the station key pair as PKCS#8 in a <see cref="ProtectedFile"/> (DPAPI, machine scope, SYSTEM/Administrators only).
/// </summary>
public sealed class DpapiStationKeyStore : IStationKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BaronDesk.StationKey.v1");

    private readonly string _path;
    private readonly ILogger<DpapiStationKeyStore> _logger;
    private readonly object _gate = new();

    public DpapiStationKeyStore(ILogger<DpapiStationKeyStore> logger)
        : this(AgentPaths.StationKeyFile, logger)
    {
    }

    internal DpapiStationKeyStore(string path, ILogger<DpapiStationKeyStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public ECDsa GetOrCreate()
    {
        lock (_gate)
        {
            return TryLoad() ?? Create();
        }
    }

    public bool Delete()
    {
        lock (_gate)
        {
            if (!ProtectedFile.Delete(_path))
            {
                return false;
            }

            _logger.LogInformation("Station key removed.");
            return true;
        }
    }

    private ECDsa? TryLoad()
    {
        byte[]? pkcs8;
        try
        {
            pkcs8 = ProtectedFile.Read(_path, Entropy);
        }
        catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning("The stored station key is unusable ({Error}); generating a new one.", ex.GetType().Name);
            return null;
        }

        if (pkcs8 is null)
        {
            return null;
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            if (key.KeySize == 256)
            {
                return key;
            }
        }
        catch (CryptographicException)
        {
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        key.Dispose();
        _logger.LogWarning("The stored station key is malformed; generating a new one.");
        return null;
    }

    private ECDsa Create()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            ProtectedFile.Write(_path, Entropy, pkcs8);
        }
        catch
        {
            key.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        _logger.LogInformation("Generated a new station key pair (ECDSA P-256, stored with DPAPI).");
        return key;
    }
}

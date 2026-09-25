using System.Security.Cryptography;
using System.Text;

namespace BaronDeskAgent.ServiceCore.Credentials;

/// <summary>
/// A bearer-style token kept in a <see cref="ProtectedFile"/>. Unreadable or malformed files are treated as absent
/// (fail closed) and the token itself is never logged.
/// </summary>
public abstract class DpapiTokenStore
{
    internal const int MaxTokenLength = 8192;

    private readonly string _path;
    private readonly byte[] _entropy;
    private readonly string _label;
    private readonly ILogger _logger;

    /// <param name="entropy">Not a secret: binds the blob to one purpose so another DPAPI blob cannot stand in for it.</param>
    /// <param name="label">Human-readable name used in log messages, e.g. "station credential".</param>
    private protected DpapiTokenStore(string path, string entropy, string label, ILogger logger)
    {
        _path = path;
        _entropy = Encoding.UTF8.GetBytes(entropy);
        _label = label;
        _logger = logger;
    }

    public string? TryGetToken()
    {
        byte[]? bytes;
        try
        {
            bytes = ProtectedFile.Read(_path, _entropy);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogError("The {Secret} file cannot be read ({Error}).", _label, ex.GetType().Name);
            return null;
        }
        catch (CryptographicException)
        {
            // Corrupted, or encrypted on another machine (cloned image). Never log the blob.
            _logger.LogError("The {Secret} cannot be decrypted on this machine; re-provision it.", _label);
            return null;
        }

        if (bytes is null)
        {
            return null;
        }

        try
        {
            var token = Encoding.UTF8.GetString(bytes);
            if (IsValidToken(token))
            {
                return token;
            }

            _logger.LogError("The stored {Secret} is malformed; re-provision it.", _label);
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
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
        try
        {
            ProtectedFile.Write(_path, _entropy, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        _logger.LogInformation("Stored the {Secret} (DPAPI, machine scope).", _label);
    }

    public bool DeleteToken()
    {
        if (!ProtectedFile.Delete(_path))
        {
            return false;
        }

        _logger.LogInformation("Removed the {Secret}.", _label);
        return true;
    }

    /// <summary>JWTs and opaque tokens are printable ASCII; anything else could corrupt an HTTP header or JSON body.</summary>
    internal static bool IsValidToken(string? token) =>
        !string.IsNullOrEmpty(token) &&
        token.Length <= MaxTokenLength &&
        token.All(c => c is > ' ' and <= '~');
}

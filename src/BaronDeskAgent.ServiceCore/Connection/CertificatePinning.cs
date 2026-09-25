using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BaronDeskAgent.ServiceCore.Configuration;

namespace BaronDeskAgent.ServiceCore.Connection;

/// <summary>
/// TLS server-certificate check shared by the WSS link and the enrollment request. There is no public CA for a LAN
/// address, so the server certificate is pinned by its SHA-256 hash.
/// </summary>
internal static class CertificatePinning
{
    /// <summary>
    /// The validation callback for <paramref name="options"/>, or null to use the default (CA-based) validation.
    /// It runs during the TLS handshake, so on mismatch not a single byte of the request (or its credential) is sent.
    /// </summary>
    public static RemoteCertificateValidationCallback? CreateCallback(AgentOptions options, ILogger logger)
    {
        if (options.GetPinnedCertificateHash() is { } pin)
        {
            return (_, certificate, _, _) => IsPinnedCertificate(certificate, pin, logger);
        }

        return options.AllowUntrustedCertificate ? (_, _, _, _) => true : null;
    }

    private static bool IsPinnedCertificate(X509Certificate? certificate, byte[] pin, ILogger logger)
    {
        if (certificate is null)
        {
            logger.LogCritical("The server presented no certificate. Aborting the connection.");
            return false;
        }

        var actual = SHA256.HashData(certificate.GetRawCertData());
        if (CryptographicOperations.FixedTimeEquals(actual, pin))
        {
            return true;
        }

        logger.LogCritical(
            "Certificate pinning mismatch (expected {Expected}, got {Actual}). Aborting the connection.",
            Convert.ToHexString(pin),
            Convert.ToHexString(actual));
        return false;
    }
}

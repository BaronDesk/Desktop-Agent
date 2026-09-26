using System.Security.Cryptography;

namespace BaronDeskAgent.ServiceCore.Credentials;

/// <summary>
/// The station's own ECDSA P-256 key pair. The private key is generated on the station and never leaves it;
/// the public key is registered with the backend at enrollment (<c>Machine.agentPublicKey</c>).
/// </summary>
public interface IStationKeyStore
{
    /// <summary>
    /// Loads the key pair, or generates and persists a new one when none is stored or the stored one is unusable
    /// (e.g. a cloned disk image: a copied identity must never be reused). The caller disposes the returned key.
    /// </summary>
    ECDsa GetOrCreate();

    /// <returns>True when a stored key was removed.</returns>
    bool Delete();
}

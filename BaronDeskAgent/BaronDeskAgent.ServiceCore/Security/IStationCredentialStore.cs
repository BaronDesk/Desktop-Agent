namespace BaronDeskAgent.ServiceCore.Security;

/// <summary>
/// Abstraction for machine-scoped station credential persistence.
/// Implementations store and retrieve the station JWT used for WebSocket upgrade authentication.
/// </summary>
public interface IStationCredentialStore
{
    /// <summary>
    /// Whether a stored token exists and can be loaded.
    /// </summary>
    bool HasToken { get; }

    /// <summary>
    /// Loads the stored station JWT from the credential store.
    /// Returns null if no token is stored or if decryption fails.
    /// </summary>
    Task<string?> LoadTokenAsync();

    /// <summary>
    /// Encrypts and persists the station JWT to the credential store.
    /// </summary>
    Task SaveTokenAsync(string jwt);

    /// <summary>
    /// Removes the stored credential, resetting the station to unenrolled state.
    /// </summary>
    Task ClearTokenAsync();
}

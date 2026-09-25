namespace BaronDeskAgent.ServiceCore.Credentials;

/// <summary>
/// Storage of the long-lived station credential (station JWT) that authenticates the WSS upgrade request.
/// </summary>
/// <remarks>
/// OPEN (roadmap branch 4, skill §15 item 8): enrollment (one-time token → admin approval → credential issued by
/// the backend) is not defined yet. Until then the credential is provisioned with
/// <c>BaronDeskAgent.ServiceCore.exe --set-station-token</c>; enrollment will call <see cref="SaveToken"/>.
/// </remarks>
public interface IStationCredentialStore
{
    /// <summary>
    /// The provisioned token, or null when none is provisioned or it cannot be read. Read on every connect,
    /// so a rotated credential is picked up without a restart.
    /// </summary>
    string? TryGetToken();

    /// <exception cref="ArgumentException">The token is empty, too long, or contains characters not allowed in an HTTP header.</exception>
    void SaveToken(string token);

    /// <returns>True when a stored credential was removed.</returns>
    bool DeleteToken();
}

namespace BaronDeskAgent.ServiceCore.Credentials;

/// <summary>
/// Storage of the one-time enrollment token an admin generates in the dashboard and provisions on the station
/// (<c>BaronDeskAgent.ServiceCore.exe --set-enrollment-token</c>). Deleted once enrollment succeeds or is rejected.
/// </summary>
public interface IEnrollmentTokenStore
{
    /// <summary>The provisioned token, or null when none is provisioned or it cannot be read.</summary>
    string? TryGetToken();

    /// <exception cref="ArgumentException">The token is empty, too long, or not printable ASCII.</exception>
    void SaveToken(string token);

    /// <returns>True when a stored token was removed.</returns>
    bool DeleteToken();
}

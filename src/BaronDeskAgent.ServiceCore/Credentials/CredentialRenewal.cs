using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Credentials;

/// <summary>
/// Applies a <c>station_credential</c> from the backend: the renewed station JWT replaces the stored one and is
/// used from the next connect (the credential is read on every connect). The token is never logged.
/// </summary>
public static class CredentialRenewal
{
    /// <returns>True when the renewed token was stored; false (logged) when it was unusable.</returns>
    public static bool TryStore(IStationCredentialStore store, StationCredentialPayload payload, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(payload.StationToken))
        {
            logger.LogWarning("Ignored a station credential renewal without a token.");
            return false;
        }

        try
        {
            store.SaveToken(payload.StationToken.Trim());
        }
        catch (ArgumentException)
        {
            // The message would echo the token: log only that it was refused.
            logger.LogWarning("Ignored a station credential renewal: the token is not a valid credential.");
            return false;
        }

        logger.LogInformation("Station credential renewed; it is used from the next connect.");
        return true;
    }
}

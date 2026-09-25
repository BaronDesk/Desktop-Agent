using BaronDeskAgent.ServiceCore.Persistence;

namespace BaronDeskAgent.ServiceCore.Credentials;

/// <summary>
/// Keeps the station credential encrypted with DPAPI, machine scope, in a file only SYSTEM and Administrators can read.
/// </summary>
/// <remarks>
/// Honest limit: an administrator or SYSTEM attacker on the station can extract the credential; the blast radius
/// is one station, and the backend can revoke and rotate it.
/// </remarks>
public sealed class DpapiStationCredentialStore : DpapiTokenStore, IStationCredentialStore
{
    public DpapiStationCredentialStore(ILogger<DpapiStationCredentialStore> logger)
        : this(AgentPaths.CredentialFile, logger)
    {
    }

    internal DpapiStationCredentialStore(string path, ILogger<DpapiStationCredentialStore> logger)
        : base(path, "BaronDesk.StationCredential.v1", "station credential", logger)
    {
    }
}

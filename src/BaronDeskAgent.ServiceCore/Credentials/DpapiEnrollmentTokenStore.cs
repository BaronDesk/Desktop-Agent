using BaronDeskAgent.ServiceCore.Persistence;

namespace BaronDeskAgent.ServiceCore.Credentials;

/// <summary>
/// Keeps the one-time enrollment token (DPAPI, machine scope) from provisioning until enrollment ends.
/// </summary>
public sealed class DpapiEnrollmentTokenStore : DpapiTokenStore, IEnrollmentTokenStore
{
    public DpapiEnrollmentTokenStore(ILogger<DpapiEnrollmentTokenStore> logger)
        : this(AgentPaths.EnrollmentTokenFile, logger)
    {
    }

    internal DpapiEnrollmentTokenStore(string path, ILogger<DpapiEnrollmentTokenStore> logger)
        : base(path, "BaronDesk.EnrollmentToken.v1", "enrollment token", logger)
    {
    }
}

namespace BaronDeskAgent.ServiceCore.Session;

/// <summary>
/// The backend session currently bound to this station. The backend owns the session (timer, billing);
/// the agent only remembers its id. Transitions are orchestrated by <see cref="StationController"/>.
/// </summary>
public sealed class SessionService
{
    private readonly ILogger<SessionService> _logger;
    private readonly object _gate = new();
    private Guid? _currentSessionId;

    public SessionService(ILogger<SessionService> logger)
    {
        _logger = logger;
    }

    public Guid? CurrentSessionId
    {
        get
        {
            lock (_gate)
            {
                return _currentSessionId;
            }
        }
    }

    public bool IsSessionActive => CurrentSessionId.HasValue;

    public void Start(Guid sessionId)
    {
        lock (_gate)
        {
            _currentSessionId = sessionId;
        }

        _logger.LogInformation("Session {SessionId} is active.", sessionId);
    }

    /// <returns>The id of the session that ended, or null if none was active.</returns>
    public Guid? End(string reason)
    {
        Guid? ended;
        lock (_gate)
        {
            ended = _currentSessionId;
            _currentSessionId = null;
        }

        if (ended is not null)
        {
            _logger.LogInformation("Session {SessionId} ended ({Reason}).", ended, reason);
        }

        return ended;
    }
}

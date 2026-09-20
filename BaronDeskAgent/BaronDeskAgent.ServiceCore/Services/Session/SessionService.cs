namespace BaronDeskAgent.ServiceCore.Services.Session;

public sealed class SessionService
{
    private readonly ILogger<SessionService> _logger;
    private readonly object _stateLock = new();

    private bool _isSessionActive;
    private Guid? _currentSessionId;
    private DateTimeOffset? _sessionStartedAt;

    public event Action<Guid>? OnSessionStarted;
    public event Action<string>? OnSessionEnded;

    public SessionService(ILogger<SessionService> logger)
    {
        _logger = logger;
    }

    public bool IsSessionActive
    {
        get
        {
            lock (_stateLock)
            {
                return _isSessionActive;
            }
        }
    }

    public Guid? CurrentSessionId
    {
        get
        {
            lock (_stateLock)
            {
                return _currentSessionId;
            }
        }
    }

    public DateTimeOffset? SessionStartedAt
    {
        get
        {
            lock (_stateLock)
            {
                return _sessionStartedAt;
            }
        }
    }

    public TimeSpan? PlayDuration
    {
        get
        {
            lock (_stateLock)
            {
                return _sessionStartedAt.HasValue
                    ? DateTimeOffset.UtcNow - _sessionStartedAt.Value
                    : null;
            }
        }
    }

    public Task StartSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_isSessionActive && _currentSessionId == sessionId)
            {
                _logger.LogInformation("Session {SessionId} is already active.", sessionId);
                return Task.CompletedTask;
            }

            _isSessionActive = true;
            _currentSessionId = sessionId;
            _sessionStartedAt = DateTimeOffset.UtcNow;
        }

        _logger.LogInformation("Session state changed to ACTIVE. SessionId={SessionId}", sessionId);
        OnSessionStarted?.Invoke(sessionId);

        return Task.CompletedTask;
    }

    public Task StartSessionAsync(CancellationToken cancellationToken = default)
    {
        return StartSessionAsync(Guid.NewGuid(), cancellationToken);
    }

    public Task EndSessionAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        Guid? endedSessionId;

        lock (_stateLock)
        {
            if (!_isSessionActive)
            {
                _logger.LogInformation("No active session to end.");
                return Task.CompletedTask;
            }

            endedSessionId = _currentSessionId;
            _isSessionActive = false;
            _currentSessionId = null;
            _sessionStartedAt = null;
        }

        _logger.LogInformation("Session state changed to ENDED. SessionId={SessionId}, Reason={Reason}",
            endedSessionId, reason);

        OnSessionEnded?.Invoke(reason);

        return Task.CompletedTask;
    }

    public Task EndSessionAsync(CancellationToken cancellationToken = default)
    {
        return EndSessionAsync("normal", cancellationToken);
    }
}
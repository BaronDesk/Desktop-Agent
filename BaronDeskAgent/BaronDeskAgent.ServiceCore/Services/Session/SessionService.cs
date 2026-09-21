using System.Security.Cryptography;
using System.Text;

namespace BaronDeskAgent.ServiceCore.Services.Session;

public sealed class SessionService
{
    private readonly ILogger<SessionService> _logger;
    private readonly object _stateLock = new();

    private bool _isSessionActive;
    private Guid? _currentSessionId;
    private DateTimeOffset? _sessionStartedAt;
    private string? _expectedPin;

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

    /// <summary>
    /// The expected PIN for unlocking this session, if required by booking.
    /// </summary>
    public string? ExpectedPin
    {
        get
        {
            lock (_stateLock)
            {
                return _expectedPin;
            }
        }
    }

    /// <summary>
    /// Whether this session requires PIN entry to unlock the workstation.
    /// </summary>
    public bool RequiresPin
    {
        get
        {
            lock (_stateLock)
            {
                return !string.IsNullOrWhiteSpace(_expectedPin);
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
        string? expectedPin = null,
        CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_isSessionActive && _currentSessionId == sessionId)
            {
                _logger.LogInformation("Session {SessionId} is already active.", sessionId);
                if (!string.IsNullOrWhiteSpace(expectedPin))
                {
                    _expectedPin = expectedPin;
                }
                return Task.CompletedTask;
            }

            _isSessionActive = true;
            _currentSessionId = sessionId;
            _sessionStartedAt = DateTimeOffset.UtcNow;
            _expectedPin = expectedPin;
        }

        _logger.LogInformation(
            "Session state changed to ACTIVE. SessionId={SessionId}, RequiresPin={RequiresPin}",
            sessionId,
            !string.IsNullOrWhiteSpace(expectedPin));

        OnSessionStarted?.Invoke(sessionId);

        return Task.CompletedTask;
    }

    public Task StartSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        return StartSessionAsync(sessionId, null, cancellationToken);
    }

    public Task StartSessionAsync(CancellationToken cancellationToken = default)
    {
        return StartSessionAsync(Guid.NewGuid(), null, cancellationToken);
    }

    /// <summary>
    /// Validates the entered PIN against the expected PIN using constant-time comparison.
    /// Clears the expected PIN upon successful verification.
    /// </summary>
    public bool ValidatePin(string? enteredPin)
    {
        lock (_stateLock)
        {
            if (string.IsNullOrWhiteSpace(_expectedPin))
            {
                return true; // No PIN required
            }

            if (string.IsNullOrWhiteSpace(enteredPin))
            {
                return false;
            }

            var expectedBytes = Encoding.UTF8.GetBytes(_expectedPin);
            var enteredBytes = Encoding.UTF8.GetBytes(enteredPin);

            bool matches = CryptographicOperations.FixedTimeEquals(expectedBytes, enteredBytes);
            if (matches)
            {
                _logger.LogInformation("PIN validation succeeded for session {SessionId}.", _currentSessionId);
                _expectedPin = null; // PIN consumed
            }
            else
            {
                _logger.LogWarning("PIN validation failed for session {SessionId}.", _currentSessionId);
            }

            return matches;
        }
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
            _expectedPin = null;
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
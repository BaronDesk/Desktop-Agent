namespace BaronDeskAgent.ServiceCore.Services.Session;

public sealed class SessionService
{
    private readonly ILogger<SessionService> _logger;

    private int _sessionActive;

    public SessionService(
        ILogger<SessionService> logger)
    {
        _logger = logger;
    }

    public bool IsSessionActive =>
        Interlocked.CompareExchange(
            ref _sessionActive,
            0,
            0) == 1;

    public Task StartSessionAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsSessionActive)
        {
            _logger.LogInformation(
                "A session is already active.");

            return Task.CompletedTask;
        }

        Interlocked.Exchange(
            ref _sessionActive,
            1);

        _logger.LogInformation(
            "Session state changed to ACTIVE.");

        return Task.CompletedTask;
    }

    public Task EndSessionAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsSessionActive)
        {
            _logger.LogInformation(
                "No active session to end.");

            return Task.CompletedTask;
        }

        Interlocked.Exchange(
            ref _sessionActive,
            0);

        _logger.LogInformation(
            "Session state changed to ENDED.");

        return Task.CompletedTask;
    }
}
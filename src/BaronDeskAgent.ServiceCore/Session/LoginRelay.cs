using System.Collections.Concurrent;
using BaronDesk.Shared.Contracts;
using BaronDesk.Shared.Ipc;
using BaronDeskAgent.ServiceCore.Connection;

namespace BaronDeskAgent.ServiceCore.Session;

public sealed record LoginRelayResult(LoginOutcome Outcome, string? Detail = null);

/// <summary>
/// Relays a credential typed on the lock screen to the backend and reports its verdict.
/// </summary>
/// <remarks>
/// Golden rule: the agent decides nothing. It never verifies a credential, never stores or logs it, and never
/// unlocks on its own. The station unlocks only when the backend sends <c>UNLOCK</c>.
/// With no backend connection the answer is <see cref="LoginOutcome.Unavailable"/>: no new sessions offline.
/// </remarks>
public sealed class LoginRelay
{
    private const string PinMethod = "pin";
    private const int MaxCredentialLength = 64;
    private const int MaxFailuresBeforeLockout = 5;

    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromSeconds(30);

    private readonly IServerConnection _connection;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LoginRelay> _logger;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<LoginResultPayload>> _pending = new();
    private readonly object _rateGate = new();

    private int _inFlight;
    private int _consecutiveFailures;
    private long? _lockedOutUntil;

    public LoginRelay(IServerConnection connection, TimeProvider timeProvider, ILogger<LoginRelay> logger)
    {
        _connection = connection;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<LoginRelayResult> SubmitAsync(string? credential, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credential) || credential.Length > MaxCredentialLength)
        {
            return new LoginRelayResult(LoginOutcome.Rejected, "Enter your PIN.");
        }

        if (RemainingLockout() is { } remaining)
        {
            return new LoginRelayResult(LoginOutcome.RateLimited, ((int)Math.Ceiling(remaining.TotalSeconds)).ToString());
        }

        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            return new LoginRelayResult(LoginOutcome.Busy);
        }

        try
        {
            if (!_connection.IsReady)
            {
                return new LoginRelayResult(LoginOutcome.Unavailable);
            }

            return await RelayAsync(credential.Trim(), cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _inFlight, 0);
        }
    }

    /// <summary>Called by the connection worker when a <c>login_result</c> arrives.</summary>
    public void Complete(LoginResultPayload result)
    {
        if (_pending.TryRemove(result.RequestId, out var waiter))
        {
            waiter.TrySetResult(result);
        }
        else
        {
            _logger.LogDebug("Ignoring login_result for unknown or expired request {RequestId}.", result.RequestId);
        }
    }

    private async Task<LoginRelayResult> RelayAsync(string credential, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid();
        var waiter = new TaskCompletionSource<LoginResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = waiter;

        try
        {
            await _connection.SendAsync(
                MessageTypes.LoginRequest,
                new LoginRequestPayload { Method = PinMethod, Credential = credential },
                AgentJsonContext.Default.LoginRequestPayload,
                cancellationToken,
                requestId);

            var result = await waiter.Task.WaitAsync(ResponseTimeout, _timeProvider, cancellationToken);
            _logger.LogInformation("Backend {Verdict} the login request {RequestId}.", result.Accepted ? "accepted" : "rejected", requestId);

            if (result.Accepted)
            {
                ResetFailures();
                return new LoginRelayResult(LoginOutcome.Accepted);
            }

            RegisterFailure();
            return new LoginRelayResult(LoginOutcome.Rejected, result.Reason);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("No login_result from the backend within {Timeout}s.", ResponseTimeout.TotalSeconds);
            return new LoginRelayResult(LoginOutcome.Timeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not relay the login request.");
            return new LoginRelayResult(LoginOutcome.Unavailable);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private TimeSpan? RemainingLockout()
    {
        lock (_rateGate)
        {
            if (_lockedOutUntil is not { } until)
            {
                return null;
            }

            var remaining = _timeProvider.GetElapsedTime(_timeProvider.GetTimestamp(), until);
            if (remaining > TimeSpan.Zero)
            {
                return remaining;
            }

            _lockedOutUntil = null;
            return null;
        }
    }

    private void RegisterFailure()
    {
        lock (_rateGate)
        {
            if (++_consecutiveFailures < MaxFailuresBeforeLockout)
            {
                return;
            }

            _consecutiveFailures = 0;
            _lockedOutUntil = _timeProvider.GetTimestamp() + (long)(LockoutDuration.TotalSeconds * _timeProvider.TimestampFrequency);
        }
    }

    private void ResetFailures()
    {
        lock (_rateGate)
        {
            _consecutiveFailures = 0;
            _lockedOutUntil = null;
        }
    }
}

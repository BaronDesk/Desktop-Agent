using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using BaronDesk.Shared.Contracts;
using BaronDesk.Shared.Ipc;
using BaronDeskAgent.ServiceCore.Configuration;
using BaronDeskAgent.ServiceCore.Connection;
using BaronDeskAgent.ServiceCore.Games;
using BaronDeskAgent.ServiceCore.Platform;
using BaronDeskAgent.ServiceCore.Session;
using BaronDeskAgent.ServiceCore.Telemetry;
using Microsoft.Extensions.Options;

namespace BaronDeskAgent.ServiceCore.Ipc;

/// <summary>
/// Named-pipe link to the LockUI helper in the user's session, and the service's <see cref="ILockScreen"/>.
/// </summary>
/// <remarks>
/// The helper has no authority: it shows what the service asks for and forwards typed credentials, which the
/// service relays to the backend. Only the console user may open the pipe, only the service may host it
/// (squatting is detected), and the connecting process is verified.
/// </remarks>
public sealed class PipeServer : BackgroundService, ILockScreen
{
    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RebindDelay = TimeSpan.FromSeconds(1);

    /// <summary>The pipe ACL names the console user; re-create it periodically so a new sign-in can connect.</summary>
    private static readonly TimeSpan AclRefreshInterval = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HelperMissingAlertDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RelaunchInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SecurityAlertInterval = TimeSpan.FromMinutes(5);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IServerConnection _connection;
    private readonly LoginRelay _loginRelay;
    private readonly TelemetryPublisher _publisher;
    private readonly InteractiveProcessLauncher _launcher;
    private readonly ServerClock _serverClock;
    private readonly AgentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PipeServer> _logger;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _confirmations = new();
    private readonly object _watchdogGate = new();
    private readonly Dictionary<string, long> _lastSecurityAlerts = new(StringComparer.Ordinal);

    private StreamWriter? _writer;
    private volatile bool _clientConnected;

    // What the overlay should show. Starts locked, so a freshly connected helper is told to lock.
    private volatile bool _desiredLocked = true;

    private int _taskManagerPolicyWarningLogged;
    private long? _helperMissingSince;
    private bool _helperMissingAlerted;
    private long? _lastRelaunchAttempt;

    public PipeServer(
        IServerConnection connection,
        LoginRelay loginRelay,
        TelemetryPublisher publisher,
        InteractiveProcessLauncher launcher,
        ServerClock serverClock,
        IOptions<AgentOptions> options,
        TimeProvider timeProvider,
        ILogger<PipeServer> logger)
    {
        _connection = connection;
        _loginRelay = loginRelay;
        _publisher = publisher;
        _launcher = launcher;
        _serverClock = serverClock;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task<OverlayResult> ShowAsync(CancellationToken cancellationToken)
    {
        _desiredLocked = true;
        ApplyTaskManagerPolicy(locked: true);
        return SendAndConfirmAsync(PipeMessageKind.ShowLock, cancellationToken);
    }

    public Task<OverlayResult> HideAsync(CancellationToken cancellationToken)
    {
        _desiredLocked = false;
        ApplyTaskManagerPolicy(locked: false);
        return SendAndConfirmAsync(PipeMessageKind.HideLock, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var watchdog = _timeProvider.CreateTimer(_ => RunWatchdog(stoppingToken), null, WatchdogInterval, WatchdogInterval);
        _connection.ReadyChanged += OnServerReadyChanged;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ServeOneClientAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "IPC pipe error; rebinding.");
                    try
                    {
                        await Task.Delay(RebindDelay, _timeProvider, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            _connection.ReadyChanged -= OnServerReadyChanged;
        }
    }

    private async Task ServeOneClientAsync(CancellationToken cancellationToken)
    {
        NamedPipeServerStream pipe;
        try
        {
            pipe = CreateServerStream();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // FirstPipeInstance: another process already hosts this pipe name and could impersonate the service.
            _logger.LogCritical(ex, "Another process owns the pipe name '{Pipe}'.", PipeConfig.Name);
            await PublishSecurityAlertAsync("pipe_squatted", $"Another process owns the lock-screen pipe name '{PipeConfig.Name}'.", cancellationToken);
            throw;
        }

        await using (pipe)
        {
            using (var waitTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                waitTimeout.CancelAfter(AclRefreshInterval);
                try
                {
                    await pipe.WaitForConnectionAsync(waitTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }

            if (!PipeClientVerifier.IsTrusted(pipe, _options.LockUiExecutablePath, out var reason))
            {
                _logger.LogWarning("Rejected IPC client: {Reason}.", reason);
                await PublishSecurityAlertAsync("pipe_client_rejected", $"Rejected lock-screen IPC client: {reason}.", cancellationToken);
                return;
            }

            await RunClientAsync(pipe, cancellationToken);
        }
    }

    private async Task RunClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        await SetWriterAsync(new StreamWriter(pipe, Utf8NoBom, bufferSize: 1024, leaveOpen: true) { AutoFlush = true });
        _clientConnected = true;
        _logger.LogInformation("LockUI helper connected.");

        try
        {
            // A helper connects after every sign-in: re-apply the policy to the (possibly new) console user.
            ApplyTaskManagerPolicy(_desiredLocked);
            await TrySendAsync(new PipeMessage { Kind = _desiredLocked ? PipeMessageKind.ShowLock : PipeMessageKind.HideLock }, cancellationToken);
            await TrySendAsync(new PipeMessage { Kind = PipeMessageKind.ServerStatus, ServerOnline = _connection.IsReady }, cancellationToken);

            while (await PipeLineReader.ReadLineAsync(reader, PipeConfig.MaxMessageChars, cancellationToken) is { } line)
            {
                HandleMessage(line, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            _logger.LogWarning(ex, "Dropping the LockUI connection.");
        }
        finally
        {
            _clientConnected = false;
            await SetWriterAsync(null);

            foreach (var confirmation in _confirmations.Values)
            {
                confirmation.TrySetResult(false);
            }

            _logger.LogInformation("LockUI helper disconnected.");
        }
    }

    private void HandleMessage(string line, CancellationToken cancellationToken)
    {
        PipeMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(line, PipeJsonContext.Default.PipeMessage);
        }
        catch (JsonException)
        {
            // The raw line is not logged: it may contain a credential.
            _logger.LogWarning("Ignored a malformed IPC message.");
            return;
        }

        switch (message?.Kind)
        {
            case PipeMessageKind.LockShown or PipeMessageKind.LockHidden:
                if (message.CorrelationId is { } id && _confirmations.TryGetValue(id, out var confirmation))
                {
                    confirmation.TrySetResult(true);
                }

                break;

            case PipeMessageKind.SubmitCredential:
                // Not awaited: the backend may take seconds to answer, and the read loop must keep
                // processing overlay confirmations in the meantime.
                _ = RelayLoginAsync(message.Credential, cancellationToken);
                break;

            case PipeMessageKind.HelperAlive:
                break;

            default:
                _logger.LogDebug("Ignored IPC message {Message}.", message);
                break;
        }
    }

    private async Task RelayLoginAsync(string? credential, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _loginRelay.SubmitAsync(credential, cancellationToken);
            await TrySendAsync(
                new PipeMessage { Kind = PipeMessageKind.LoginResult, LoginOutcome = result.Outcome, Detail = result.Detail },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login relay failed.");
        }
    }

    private async Task<OverlayResult> SendAndConfirmAsync(PipeMessageKind kind, CancellationToken cancellationToken)
    {
        if (!_clientConnected)
        {
            _logger.LogWarning("Cannot {Kind}: the LockUI helper is not connected (is BaronDesk.LockUI running?).", kind);
            return OverlayResult.HelperNotConnected;
        }

        var correlationId = Guid.NewGuid();
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _confirmations[correlationId] = confirmation;

        try
        {
            if (!await TrySendAsync(new PipeMessage { Kind = kind, CorrelationId = correlationId }, cancellationToken))
            {
                return OverlayResult.HelperNotConnected;
            }

            return await confirmation.Task.WaitAsync(ConfirmationTimeout, _timeProvider, cancellationToken)
                ? OverlayResult.Confirmed
                : OverlayResult.NotConfirmed;
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("The LockUI helper did not confirm {Kind} within {Timeout}s.", kind, ConfirmationTimeout.TotalSeconds);
            return OverlayResult.NotConfirmed;
        }
        finally
        {
            _confirmations.TryRemove(correlationId, out _);
        }
    }

    private async Task<bool> TrySendAsync(PipeMessage message, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (_writer is null)
            {
                return false;
            }

            await _writer.WriteLineAsync(JsonSerializer.Serialize(message, PipeJsonContext.Default.PipeMessage).AsMemory(), cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Could not send {Kind} to the LockUI helper.", message.Kind);
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SetWriterAsync(StreamWriter? writer)
    {
        await _sendLock.WaitAsync(CancellationToken.None);
        try
        {
            if (_writer is not null)
            {
                await _writer.DisposeAsync();
            }

            _writer = writer;
        }
        catch (IOException)
        {
            _writer = writer;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void ApplyTaskManagerPolicy(bool locked)
    {
        if (TaskManagerPolicy.TryApply(disabled: locked, out var error))
        {
            return;
        }

        // Expected when developing (the agent runs as a standard user); logged once to avoid noise.
        if (Interlocked.Exchange(ref _taskManagerPolicyWarningLogged, 1) == 0)
        {
            _logger.LogWarning(
                "Task Manager policy could not be applied for the console user ({Error}). The service must run as LocalSystem or an administrator.",
                error);
        }
    }

    private void OnServerReadyChanged(bool ready) =>
        _ = TrySendAsync(new PipeMessage { Kind = PipeMessageKind.ServerStatus, ServerOnline = ready }, CancellationToken.None);

    /// <summary>Raises an alert when the helper stays missing while locked, and relaunches it when configured.</summary>
    private void RunWatchdog(CancellationToken cancellationToken)
    {
        var raiseAlert = false;
        var relaunch = false;

        lock (_watchdogGate)
        {
            if (_clientConnected)
            {
                _helperMissingSince = null;
                _helperMissingAlerted = false;
                return;
            }

            var now = _timeProvider.GetTimestamp();
            _helperMissingSince ??= now;

            if (_desiredLocked && !_helperMissingAlerted && _timeProvider.GetElapsedTime(_helperMissingSince.Value, now) >= HelperMissingAlertDelay)
            {
                _helperMissingAlerted = true;
                raiseAlert = true;
            }

            if (_lastRelaunchAttempt is not { } last || _timeProvider.GetElapsedTime(last, now) >= RelaunchInterval)
            {
                _lastRelaunchAttempt = now;
                relaunch = true;
            }
        }

        if (relaunch)
        {
            TryRelaunchHelper();
        }

        if (raiseAlert)
        {
            _logger.LogError("The lock screen helper is not running while the station is locked.");
            _ = PublishSecurityAlertAsync("helper_missing", "The lock screen helper is not running while the station is locked.", cancellationToken);
        }
    }

    private void TryRelaunchHelper()
    {
        // Only a service can launch into the user's session; in development the helper is started by hand.
        if (string.IsNullOrWhiteSpace(_options.LockUiExecutablePath) || !InteractiveSession.IsServiceSession)
        {
            return;
        }

        try
        {
            using var process = _launcher.Launch(_options.LockUiExecutablePath, arguments: null, workingDirectory: null);
            _logger.LogWarning("Relaunched the LockUI helper (Pid={Pid}).", process.Id);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "LockUI helper not relaunched (nobody signed in?).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not relaunch the LockUI helper.");
        }
    }

    private async Task PublishSecurityAlertAsync(string key, string detail, CancellationToken cancellationToken)
    {
        lock (_watchdogGate)
        {
            var now = _timeProvider.GetTimestamp();
            if (_lastSecurityAlerts.TryGetValue(key, out var last) && _timeProvider.GetElapsedTime(last, now) < SecurityAlertInterval)
            {
                return;
            }

            _lastSecurityAlerts[key] = now;
        }

        try
        {
            await _publisher.PublishAlertAsync(
                new AlertPayload
                {
                    Category = AlertCategories.SecurityViolation,
                    Type = AlertTypes.HardwareFailure,
                    Severity = AlertSeverities.High,
                    Detail = detail,
                    OccurredAt = _serverClock.UtcNow
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not queue a security alert.");
        }
    }

    private static NamedPipeServerStream CreateServerStream()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var service = identity.User!;

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(service, PipeAccessRights.FullControl, AccessControlType.Allow));

        // Read/write only: no CreateNewInstance, so nobody else can host an instance of this pipe.
        var helperUser = InteractiveSession.IsServiceSession ? InteractiveSession.TryGetConsoleUserSid() : service;
        if (helperUser is not null && helperUser != service)
        {
            security.AddAccessRule(new PipeAccessRule(helperUser, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            PipeConfig.Name,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }
}

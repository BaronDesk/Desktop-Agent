using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Services.Commands;
using BaronDeskAgent.ServiceCore.Services.Session;

namespace BaronDeskAgent.ServiceCore.Ipc;

/// <summary>
/// Named-pipe IPC server hosting bidirectional communication between
/// BaronDeskAgent.ServiceCore and BaronDesk.LockUI.
/// </summary>
public sealed class PipeServer : BackgroundService
{
    private readonly LockService _lockService;
    private readonly SessionService _sessionService;
    private readonly LeaseManager _leaseManager;
    private readonly ILogger<PipeServer> _logger;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private StreamWriter? _currentWriter;
    private NamedPipeServerStream? _currentPipe;

    // Brute-force protection
    private int _failedPinAttempts;
    private DateTimeOffset _pinLockoutUntil = DateTimeOffset.MinValue;
    private const int MaxPinAttempts = 5;
    private static readonly TimeSpan PinLockoutDuration = TimeSpan.FromSeconds(30);

    public PipeServer(
        LockService lockService,
        SessionService sessionService,
        LeaseManager leaseManager,
        ILogger<PipeServer> logger)
    {
        _lockService = lockService;
        _sessionService = sessionService;
        _leaseManager = leaseManager;
        _logger = logger;

        _lockService.OnLockStateChanged += async (isLocked, commandId, ct) =>
        {
            if (isLocked)
            {
                await SendShowLockAsync(commandId, ct);
            }
            else
            {
                await SendHideLockAsync(commandId, ct);
            }
        };
    }

    public bool IsClientConnected =>
        _currentPipe is { IsConnected: true };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PipeServer started on named pipe '{PipeName}'.", PipeConfig.Name);

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreateServerStream();

                _logger.LogDebug("Waiting for LockUI connection on '{PipeName}'...", PipeConfig.Name);
                await pipe.WaitForConnectionAsync(stoppingToken);

                _logger.LogInformation("LockUI helper connected to IPC pipe.");

                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

                await _sendLock.WaitAsync(stoppingToken);
                try
                {
                    _currentPipe = pipe;
                    _currentWriter = writer;
                }
                finally
                {
                    _sendLock.Release();
                }

                // Initial synchronization: align LockUI with current LockService state
                if (_lockService.IsLocked)
                {
                    await SendAsync(PipeMessageKind.ShowLock, null, null, stoppingToken);
                }
                else
                {
                    await SendAsync(PipeMessageKind.HideLock, null, null, stoppingToken);
                }

                // Listen loop for incoming messages from LockUI
                while (pipe.IsConnected && !stoppingToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(stoppingToken);
                    if (line is null) break;

                    await HandleIncomingMessageAsync(line, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception in IPC PipeServer loop. Rebinding pipe in 1s...");
                try
                {
                    await Task.Delay(1000, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
            finally
            {
                await _sendLock.WaitAsync(CancellationToken.None);
                try
                {
                    _currentWriter = null;
                    _currentPipe = null;
                }
                finally
                {
                    _sendLock.Release();
                }

                if (pipe is not null)
                {
                    try
                    {
                        if (pipe.IsConnected)
                        {
                            pipe.Disconnect();
                        }
                        pipe.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error disposing previous named pipe server instance.");
                    }
                }
                _logger.LogInformation("LockUI helper disconnected from IPC pipe.");
            }
        }

        _logger.LogInformation("PipeServer stopped.");
    }

    private async Task HandleIncomingMessageAsync(string json, CancellationToken cancellationToken)
    {
        PipeMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<PipeMessage>(json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse inbound PipeMessage: {Json}", json);
            return;
        }

        if (message is null) return;

        switch (message.Kind)
        {
            case PipeMessageKind.LockShown:
                _logger.LogInformation("LockUI confirmed: screen LOCKED (CommandId={CommandId}).", message.CommandId);
                break;

            case PipeMessageKind.LockHidden:
                _logger.LogInformation("LockUI confirmed: screen UNLOCKED (CommandId={CommandId}).", message.CommandId);
                break;

            case PipeMessageKind.HelperAlive:
                _logger.LogDebug("LockUI helper alive heartbeat received.");
                break;

            case PipeMessageKind.SubmitPin:
                await HandlePinSubmissionAsync(message.Payload, cancellationToken);
                break;

            default:
                _logger.LogDebug("Received unhandled PipeMessageKind: {Kind}", message.Kind);
                break;
        }
    }

    private async Task HandlePinSubmissionAsync(string? enteredPin, CancellationToken cancellationToken)
    {
        // Brute-force rate limiting
        if (DateTimeOffset.UtcNow < _pinLockoutUntil)
        {
            var remaining = (int)(_pinLockoutUntil - DateTimeOffset.UtcNow).TotalSeconds;
            _logger.LogWarning("PIN submission rejected — lockout active for {Remaining}s.", remaining);
            await SendAsync(PipeMessageKind.PinResult, null, $"LOCKED_OUT:{remaining}", cancellationToken);
            return;
        }

        _logger.LogInformation("Received PIN submission via LockUI IPC.");

        bool isValid = _sessionService.ValidatePin(enteredPin);
        if (isValid)
        {
            _logger.LogInformation("PIN validation succeeded via IPC! Unlocking workstation.");
            Interlocked.Exchange(ref _failedPinAttempts, 0);

            await SendAsync(PipeMessageKind.PinResult, null, "SUCCESS", cancellationToken);
            await _lockService.UnlockAsync(null, cancellationToken);

            // Grant lease only after successful PIN entry (Finding #7)
            _leaseManager.UpdateLease(null);
        }
        else
        {
            var attempts = Interlocked.Increment(ref _failedPinAttempts);
            _logger.LogWarning("PIN validation failed via IPC. Attempt #{Attempts}.", attempts);

            if (attempts >= MaxPinAttempts)
            {
                _pinLockoutUntil = DateTimeOffset.UtcNow.Add(PinLockoutDuration);
                Interlocked.Exchange(ref _failedPinAttempts, 0);
                _logger.LogWarning(
                    "Too many failed PIN attempts. Locked out for {Duration}s.",
                    PinLockoutDuration.TotalSeconds);
                await SendAsync(PipeMessageKind.PinResult, null,
                    $"LOCKED_OUT:{(int)PinLockoutDuration.TotalSeconds}", cancellationToken);
            }
            else
            {
                await SendAsync(PipeMessageKind.PinResult, null, "INVALID_PIN", cancellationToken);
            }
        }
    }

    public async Task SendShowLockAsync(Guid? commandId = null, CancellationToken cancellationToken = default)
    {
        await SendAsync(PipeMessageKind.ShowLock, commandId, null, cancellationToken);
    }

    public async Task SendHideLockAsync(Guid? commandId = null, CancellationToken cancellationToken = default)
    {
        await SendAsync(PipeMessageKind.HideLock, commandId, null, cancellationToken);
    }

    private async Task SendAsync(
        PipeMessageKind kind,
        Guid? commandId,
        string? payload,
        CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (_currentWriter is null || _currentPipe is not { IsConnected: true })
            {
                _logger.LogDebug("PipeServer cannot send {Kind}: LockUI is not connected.", kind);
                return;
            }

            var message = new PipeMessage
            {
                Kind = kind,
                CommandId = commandId,
                Timestamp = DateTime.UtcNow,
                Payload = payload
            };

            var json = JsonSerializer.Serialize(message);
            await _currentWriter.WriteLineAsync(json.AsMemory(), cancellationToken);
            _logger.LogDebug("PipeServer sent {Kind} to LockUI (CommandId={CommandId}).", kind, commandId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send {Kind} message over IPC pipe.", kind);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static NamedPipeServerStream CreateServerStream()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var pipeSecurity = new PipeSecurity();

            // Allow current user / SYSTEM full control
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                WindowsIdentity.GetCurrent().User ?? new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

            // Allow interactive users (Session 1 desktop users) Read/Write access so LockUI can connect
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));

            return NamedPipeServerStreamAcl.Create(
                PipeConfig.Name,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 0,
                pipeSecurity);
        }

        return new NamedPipeServerStream(
            PipeConfig.Name,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }
}

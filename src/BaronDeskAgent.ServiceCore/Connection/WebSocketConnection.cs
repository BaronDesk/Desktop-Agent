using System.Buffers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Configuration;
using BaronDeskAgent.ServiceCore.Credentials;
using Microsoft.Extensions.Options;

namespace BaronDeskAgent.ServiceCore.Connection;

public sealed class WebSocketConnection : IServerConnection, IDisposable
{
    /// <summary>Largest inbound frame accepted. Commands are a few hundred bytes.</summary>
    internal const int MaxInboundMessageBytes = 64 * 1024;

    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);

    private readonly AgentOptions _options;
    private readonly IStationCredentialStore _credentials;
    private readonly Uri _serverUri;
    private readonly byte[]? _pinnedCertificateHash;
    private readonly ServerClock _serverClock;
    private readonly ILogger<WebSocketConnection> _logger;

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ArrayBufferWriter<byte> _sendBuffer = new(4096);
    private readonly Utf8JsonWriter _jsonWriter;
    private readonly byte[] _receiveBuffer = new byte[MaxInboundMessageBytes];

    private readonly object _readyGate = new();
    private volatile bool _isReady;
    private TaskCompletionSource _readyTcs = NewReadyTcs();

    private volatile ClientWebSocket? _webSocket;
    private long _outboundSeq;

    public WebSocketConnection(
        IOptions<AgentOptions> options,
        IStationCredentialStore credentials,
        ServerClock serverClock,
        ILogger<WebSocketConnection> logger)
    {
        _options = options.Value;
        _credentials = credentials;
        _serverUri = new Uri(_options.ServerUrl);
        _pinnedCertificateHash = _options.GetPinnedCertificateHash();
        _serverClock = serverClock;
        _logger = logger;
        _jsonWriter = new Utf8JsonWriter(_sendBuffer);

        if (_pinnedCertificateHash is null && _options.AllowUntrustedCertificate)
        {
            _logger.LogWarning("AllowUntrustedCertificate is on: server certificates are NOT validated (Development only).");
        }
    }

    public event Action<bool>? ReadyChanged;

    public bool IsReady => _isReady && _webSocket is { State: WebSocketState.Open };

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        lock (_readyGate)
        {
            return _readyTcs.Task.WaitAsync(cancellationToken);
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_webSocket is { State: WebSocketState.Open })
            {
                return;
            }

            DisposeSocket(_webSocket);
            _webSocket = null;

            var socket = CreateSocket();
            try
            {
                _logger.LogInformation("Connecting to {Uri}...", _serverUri);
                await socket.ConnectAsync(_serverUri, cancellationToken);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            // No sender can run yet (_webSocket is null), so resetting the per-connection sequence is safe.
            _outboundSeq = 0;
            _webSocket = socket;
            _logger.LogInformation("Connected to {Uri}.", _serverUri);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public void MarkReady() => SetReady(true);

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            SetReady(false);

            var socket = _webSocket;
            _webSocket = null;
            if (socket is null)
            {
                return;
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(CloseTimeout);
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Agent disconnecting", timeout.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "WebSocket close handshake did not complete.");
                }
            }

            DisposeSocket(socket);
            _logger.LogInformation("Disconnected from the server.");
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task<Guid> SendAsync<T>(
        string type,
        T payload,
        JsonTypeInfo<T> payloadTypeInfo,
        CancellationToken cancellationToken,
        Guid? messageId = null)
    {
        var id = messageId ?? Guid.NewGuid();

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            var socket = _webSocket;
            if (socket is not { State: WebSocketState.Open })
            {
                throw new WebSocketException(WebSocketError.InvalidState, "Not connected to the server.");
            }

            var seq = ++_outboundSeq;
            WriteEnvelope(type, id, seq, payload, payloadTypeInfo);
            await socket.SendAsync(_sendBuffer.WrittenMemory, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

            _logger.LogDebug("Sent {Type} (Id={Id}, Seq={Seq}).", type, id, seq);
        }
        finally
        {
            _sendLock.Release();
        }

        return id;
    }

    public async Task<ReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
    {
        var socket = _webSocket;
        if (socket is not { State: WebSocketState.Open })
        {
            return ReceiveResult.Closed;
        }

        var count = 0;
        var tooLarge = false;
        ValueWebSocketReceiveResult result;

        do
        {
            if (count == _receiveBuffer.Length)
            {
                // Keep reading to drain the oversized message, then report it as malformed.
                tooLarge = true;
                count = 0;
            }

            result = await socket.ReceiveAsync(_receiveBuffer.AsMemory(count), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                _logger.LogInformation(
                    "Server closed the connection ({Status}: {Description}).",
                    socket.CloseStatus,
                    socket.CloseStatusDescription);
                return ReceiveResult.Closed;
            }

            count += result.Count;
        }
        while (!result.EndOfMessage);

        if (tooLarge)
        {
            return ReceiveResult.Malformed($"Message exceeds {MaxInboundMessageBytes} bytes.");
        }

        if (result.MessageType != WebSocketMessageType.Text)
        {
            return ReceiveResult.Malformed("Binary frames are not part of the protocol.");
        }

        try
        {
            var envelope = JsonSerializer.Deserialize(
                _receiveBuffer.AsSpan(0, count),
                AgentJsonContext.Default.EnvelopeJsonElement);

            return envelope is null || string.IsNullOrWhiteSpace(envelope.Type) || envelope.Id == Guid.Empty
                ? ReceiveResult.Malformed("Envelope is missing its type or id.")
                : ReceiveResult.Message(envelope);
        }
        catch (JsonException ex)
        {
            return ReceiveResult.Malformed($"Invalid envelope JSON: {ex.Message}");
        }
    }

    public void Dispose()
    {
        DisposeSocket(_webSocket);
        _webSocket = null;
        _jsonWriter.Dispose();
        _sendLock.Dispose();
        _connectLock.Dispose();
    }

    private void WriteEnvelope<T>(string type, Guid id, long seq, T payload, JsonTypeInfo<T> payloadTypeInfo)
    {
        _sendBuffer.ResetWrittenCount();
        _jsonWriter.Reset(_sendBuffer);

        _jsonWriter.WriteStartObject();
        _jsonWriter.WriteString("type", type);
        _jsonWriter.WriteString("id", id);
        _jsonWriter.WriteString("ts", _serverClock.UtcNow);
        _jsonWriter.WriteNumber("seq", seq);
        _jsonWriter.WritePropertyName("payload");
        JsonSerializer.Serialize(_jsonWriter, payload, payloadTypeInfo);
        _jsonWriter.WriteEndObject();
        _jsonWriter.Flush();
    }

    private ClientWebSocket CreateSocket()
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(_options.KeepAliveIntervalSeconds);

        if (_pinnedCertificateHash is { } pin)
        {
            // Runs during the TLS handshake, before the HTTP upgrade: on mismatch no credential is ever sent.
            socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) => IsPinnedCertificate(certificate, pin);
        }
        else if (_options.AllowUntrustedCertificate)
        {
            socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        // Read on every connect so a rotated credential is used without a restart. The configuration value is a
        // Development-only fallback (rejected at startup elsewhere).
        var token = _credentials.TryGetToken() ?? _options.StationToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("No station credential is provisioned; the server will not accept this station until it is.");
        }
        else
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        }

        return socket;
    }

    private bool IsPinnedCertificate(X509Certificate? certificate, byte[] pin)
    {
        if (certificate is null)
        {
            _logger.LogCritical("The server presented no certificate. Aborting the connection.");
            return false;
        }

        var actual = SHA256.HashData(certificate.GetRawCertData());
        if (CryptographicOperations.FixedTimeEquals(actual, pin))
        {
            return true;
        }

        _logger.LogCritical(
            "Certificate pinning mismatch (expected {Expected}, got {Actual}). Aborting the connection.",
            Convert.ToHexString(pin),
            Convert.ToHexString(actual));
        return false;
    }

    private void SetReady(bool ready)
    {
        lock (_readyGate)
        {
            if (_isReady == ready)
            {
                return;
            }

            _isReady = ready;
            if (ready)
            {
                _readyTcs.TrySetResult();
            }
            else
            {
                _readyTcs = NewReadyTcs();
            }
        }

        try
        {
            ReadyChanged?.Invoke(ready);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A connection-state subscriber failed.");
        }
    }

    private void DisposeSocket(ClientWebSocket? socket)
    {
        try
        {
            socket?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing a WebSocket.");
        }
    }

    private static TaskCompletionSource NewReadyTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

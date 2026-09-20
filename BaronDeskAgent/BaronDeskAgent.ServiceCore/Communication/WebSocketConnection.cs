using System.Buffers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Configuration;
using BaronDeskAgent.ServiceCore.Services.Enrollment;
using Microsoft.Extensions.Options;

namespace BaronDeskAgent.ServiceCore.Communication;

public sealed class WebSocketConnection : IServerConnection, IDisposable
{
    private readonly IOptions<AgentOptions> _options;
    private readonly EnrollmentService _enrollmentService;
    private readonly ILogger<WebSocketConnection> _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private ClientWebSocket? _webSocket;
    private long _outboundSeq;
    private bool _disposed;

    public WebSocketConnection(
        IOptions<AgentOptions> options,
        EnrollmentService enrollmentService,
        ILogger<WebSocketConnection> logger)
    {
        _options = options;
        _enrollmentService = enrollmentService;
        _logger = logger;
    }

    public bool IsConnected =>
        _webSocket is { State: WebSocketState.Open };

    public long NextSequence() =>
        Interlocked.Increment(ref _outboundSeq);

    public void ResetSequence() =>
        Interlocked.Exchange(ref _outboundSeq, 0);

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected)
            {
                return;
            }

            CleanupSocket();

            var ws = new ClientWebSocket();

            ConfigureTlsAndCertPinning(ws);
            ConfigureHeadersAndOptions(ws);

            var serverUri = new Uri(_options.Value.ServerUrl);
            _logger.LogInformation("Connecting to BaronDesk server at {Uri}...", serverUri);

            await ws.ConnectAsync(serverUri, cancellationToken);

            ResetSequence();
            _webSocket = ws;

            _logger.LogInformation("Successfully connected to BaronDesk server at {Uri}.", serverUri);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to connect to BaronDesk server at {Url}.", _options.Value.ServerUrl);
            CleanupSocket();
            throw;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_webSocket is { State: WebSocketState.Open or WebSocketState.CloseReceived })
            {
                try
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Agent disconnecting", linkedCts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Notice during WebSocket close handshake.");
                }
            }

            CleanupSocket();
            _logger.LogInformation("Disconnected from BaronDesk server.");
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task SendAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var ws = _webSocket;
        if (ws is null || ws.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not connected.");
        }

        var envelopeToSend = envelope.Seq > 0 ? envelope : envelope with { Seq = NextSequence() };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelopeToSend, AgentJsonContext.Default.Envelope);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }

        _logger.LogDebug(
            "Sent envelope. Type={Type}, Id={Id}, Seq={Seq}",
            envelopeToSend.Type,
            envelopeToSend.Id,
            envelopeToSend.Seq);
    }

    public async Task SendAsync<T>(Envelope<T> envelope, CancellationToken cancellationToken)
    {
        var ws = _webSocket;
        if (ws is null || ws.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not connected.");
        }

        var envelopeToSend = envelope.Seq > 0 ? envelope : envelope with { Seq = NextSequence() };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelopeToSend, typeof(Envelope<T>), AgentJsonContext.Default);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }

        _logger.LogDebug(
            "Sent typed envelope. Type={Type}, Id={Id}, Seq={Seq}",
            envelopeToSend.Type,
            envelopeToSend.Id,
            envelopeToSend.Seq);
    }

    public async Task<Envelope<JsonElement>?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var ws = _webSocket;
        if (ws is null || ws.State != WebSocketState.Open)
        {
            return null;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        using var ms = new MemoryStream();

        try
        {
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation(
                        "Server closed WebSocket connection. Status={Status}, Description={Description}",
                        result.CloseStatus,
                        result.CloseStatusDescription);
                    return null;
                }

                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (ms.Length == 0)
            {
                return null;
            }

            var envelope = JsonSerializer.Deserialize(ms.ToArray(), AgentJsonContext.Default.EnvelopeJsonElement);
            return envelope;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ConfigureTlsAndCertPinning(ClientWebSocket ws)
    {
        var pinnedHash = _options.Value.PinnedCertificateHash;

        if (!string.IsNullOrWhiteSpace(pinnedHash))
        {
            var cleanHex = pinnedHash.Replace(":", string.Empty).Replace(" ", string.Empty).Trim();
            var expectedBytes = Convert.FromHexString(cleanHex);

            ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            {
                if (cert is null)
                {
                    _logger.LogError("Server presented null certificate during TLS handshake.");
                    return false;
                }

                byte[] actualBytes = SHA256.HashData(cert.GetRawCertData());
                bool matches = CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);

                if (!matches)
                {
                    _logger.LogCritical(
                        "Certificate pinning mismatch! Expected={Expected}, Actual={Actual}. Aborting connection.",
                        Convert.ToHexString(expectedBytes),
                        Convert.ToHexString(actualBytes));
                }

                return matches;
            };
        }
        else if (_options.Value.AllowUntrustedCertificate)
        {
            _logger.LogWarning("AllowUntrustedCertificate is enabled. Skipping TLS certificate validation.");
            ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
    }

    private void ConfigureHeadersAndOptions(ClientWebSocket ws)
    {
        // Priority: DPAPI-stored JWT (enrolled) > appsettings fallback > no header
        var token = _enrollmentService.StationJwt ?? _options.Value.StationToken;

        if (!string.IsNullOrWhiteSpace(token))
        {
            ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");
            _logger.LogDebug("Authorization header set for WebSocket upgrade. Source={Source}",
                _enrollmentService.StationJwt is not null ? "DPAPI" : "appsettings");
        }

        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(_options.Value.KeepAliveIntervalSeconds);
    }

    private void CleanupSocket()
    {
        try
        {
            _webSocket?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Exception disposing previous ClientWebSocket.");
        }
        finally
        {
            _webSocket = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CleanupSocket();
        _sendLock.Dispose();
        _connectLock.Dispose();
    }
}

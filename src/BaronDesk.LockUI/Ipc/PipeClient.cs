using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BaronDesk.Shared.Ipc;

namespace BaronDesk.LockUI.Ipc;

/// <summary>
/// Client end of the service ↔ LockUI named pipe. Reconnects forever; events are raised on a pool thread.
/// </summary>
public sealed class PipeClient
{
    private const int ConnectTimeoutMs = 5000;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AliveInterval = TimeSpan.FromSeconds(15);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private StreamWriter? _writer;

    public event Action<PipeMessage>? MessageReceived;

    public event Action<bool>? ConnectionChanged;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or UnauthorizedAccessException)
            {
                // Service not running yet, restarting, or the connection dropped.
                Trace.TraceWarning($"LockUI pipe: {ex.Message}");
            }
            catch (Exception ex)
            {
                // Nothing else may end this loop: without it the helper stays "service unavailable" for good,
                // even after the service comes back.
                Trace.TraceError($"LockUI pipe: unexpected error, reconnecting: {ex}");
            }

            try
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <returns>False when not connected or the write failed.</returns>
    public async Task<bool> SendAsync(PipeMessage message)
    {
        await _sendLock.WaitAsync();
        try
        {
            if (_writer is null)
            {
                return false;
            }

            await _writer.WriteLineAsync(JsonSerializer.Serialize(message, PipeJsonContext.Default.PipeMessage));
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task RunConnectionAsync(CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeConfig.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(ConnectTimeoutMs, cancellationToken);

        if (!PipeServerVerifier.IsTrustedServer(pipe))
        {
            Trace.TraceError("LockUI pipe: the pipe server is not the BaronDesk service; refusing to talk to it.");
            return;
        }

        using var reader = new StreamReader(pipe, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        await SetWriterAsync(new StreamWriter(pipe, Utf8NoBom, bufferSize: 1024, leaveOpen: true) { AutoFlush = true });

        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var aliveLoop = SendAliveLoopAsync(connection.Token);
        ConnectionChanged?.Invoke(true);

        try
        {
            while (await PipeLineReader.ReadLineAsync(reader, PipeConfig.MaxMessageChars, cancellationToken) is { } line)
            {
                if (TryParse(line) is { } message)
                {
                    MessageReceived?.Invoke(message);
                }
            }
        }
        finally
        {
            // Stop this connection's keep-alive loop before the next connection starts its own.
            await connection.CancelAsync();
            await aliveLoop;
            await SetWriterAsync(null);
            ConnectionChanged?.Invoke(false);
        }
    }

    private async Task SendAliveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(AliveInterval, cancellationToken);
                await SendAsync(new PipeMessage { Kind = PipeMessageKind.HelperAlive });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SetWriterAsync(StreamWriter? writer)
    {
        await _sendLock.WaitAsync();
        try
        {
            if (_writer is not null)
            {
                await _writer.DisposeAsync();
            }
        }
        catch (IOException)
        {
        }
        finally
        {
            _writer = writer;
            _sendLock.Release();
        }
    }

    private static PipeMessage? TryParse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, PipeJsonContext.Default.PipeMessage);
        }
        catch (JsonException)
        {
            Trace.TraceWarning("LockUI pipe: ignored a malformed message.");
            return null;
        }
    }

    /// <summary>Makes sure a local process squatting the pipe name cannot drive the overlay.</summary>
    private static class PipeServerVerifier
    {
        public static bool IsTrustedServer(NamedPipeClientStream pipe)
        {
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverProcessId))
            {
                return false;
            }

#if DEBUG
            // Development: the agent runs as a console app in the user's own session.
            return true;
#else
            // Production: the agent is a Windows service, and only services run in Session 0.
            return ProcessIdToSessionId(serverProcessId, out var sessionId) && sessionId == 0;
#endif
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(SafeHandle pipe, out uint serverProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    }
}

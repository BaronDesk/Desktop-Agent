using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BaronDesk.Shared.Contracts;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BaronDesk.LockUI.Ipc
{
    public class PipeClient
    {
        private readonly Action<Guid?> _onShowLock;
        private readonly Action<Guid?> _onHideLock;
        private readonly Action<bool, string?>? _onPinResult;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private StreamWriter? _writer;

        public PipeClient(
            Action<Guid?> onShowLock,
            Action<Guid?> onHideLock,
            Action<bool, string?>? onPinResult = null)
        {
            _onShowLock = onShowLock;
            _onHideLock = onHideLock;
            _onPinResult = onPinResult;
        }

        public async Task ConnectAndListenAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(".", PipeConfig.Name, PipeDirection.InOut);
                    await pipe.ConnectAsync(5000, token);

                    using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                    using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

                    await _sendLock.WaitAsync(token);
                    try
                    {
                        _writer = writer;
                    }
                    finally
                    {
                        _sendLock.Release();
                    }

                    _ = SendAliveLoopAsync(token);

                    while (pipe.IsConnected && !token.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(token);
                        if (line is null) break;

                        HandleIncoming(line);
                    }
                }
                catch (Exception)
                {
                    // Service Core not up yet, or pipe dropped mid-session — retry
                }
                finally
                {
                    await _sendLock.WaitAsync(CancellationToken.None);
                    try
                    {
                        _writer = null;
                    }
                    finally
                    {
                        _sendLock.Release();
                    }
                }

                try
                {
                    await Task.Delay(2000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private void HandleIncoming(string json)
        {
            PipeMessage? msg;
            try { msg = JsonSerializer.Deserialize<PipeMessage>(json); }
            catch (JsonException) { return; }
            if (msg is null) return;

            switch (msg.Kind)
            {
                case PipeMessageKind.ShowLock:
                    _onShowLock(msg.CommandId);
                    break;
                case PipeMessageKind.HideLock:
                    _onHideLock(msg.CommandId);
                    break;
                case PipeMessageKind.PinResult:
                    bool isSuccess = string.Equals(msg.Payload, "SUCCESS", StringComparison.OrdinalIgnoreCase);
                    _onPinResult?.Invoke(isSuccess, msg.Payload);
                    break;
            }
        }

        public async Task SendAsync(PipeMessageKind kind, Guid? commandId, string? payload = null)
        {
            await _sendLock.WaitAsync();
            try
            {
                if (_writer is null) return;
                var msg = new PipeMessage { Kind = kind, CommandId = commandId, Payload = payload };
                var json = JsonSerializer.Serialize(msg);
                await _writer.WriteLineAsync(json);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public Task SubmitPinAsync(string pin)
        {
            return SendAsync(PipeMessageKind.SubmitPin, null, pin);
        }

        private async Task SendAliveLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), token);
                await SendAsync(PipeMessageKind.HelperAlive, null);
            }
        }
    }
}
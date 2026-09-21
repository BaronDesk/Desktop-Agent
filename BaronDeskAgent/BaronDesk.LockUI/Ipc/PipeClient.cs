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
        private StreamWriter? _writer;

        public PipeClient(Action<Guid?> onShowLock, Action<Guid?> onHideLock)
        {
            _onShowLock = onShowLock;
            _onHideLock = onHideLock;
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
                    _writer = writer;

                    _ = SendAliveLoopAsync(token);

                    while (pipe.IsConnected && !token.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync();
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
                    _writer = null;
                }

                await Task.Delay(2000, token);
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
            }
        }

        public async Task SendAsync(PipeMessageKind kind, Guid? commandId)
        {
            if (_writer is null) return;
            var msg = new PipeMessage { Kind = kind, CommandId = commandId };
            var json = JsonSerializer.Serialize(msg);
            await _writer.WriteLineAsync(json);
        }

        private async Task SendAliveLoopAsync(CancellationToken token)
        {
            while (_writer is not null && !token.IsCancellationRequested)
            {
                await SendAsync(PipeMessageKind.HelperAlive, null);
                await Task.Delay(TimeSpan.FromSeconds(15), token);
            }
        }
    }
}
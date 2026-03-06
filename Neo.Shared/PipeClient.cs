using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Neo.IPC;

namespace Neo.Shared
{
    public sealed class PipeClient : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly FramedPipeMessenger _messenger;

        public bool IsConnected => _pipe.IsConnected;

        public PipeClient(string pipeName)
        {
            _pipe = new NamedPipeClientStream(
                ".", pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            _messenger = new FramedPipeMessenger(_pipe);
        }

        public Task ConnectAsync(CancellationToken ct = default) => _pipe.ConnectAsync(ct);

        public Task SendAsync(IpcEnvelope env, CancellationToken ct = default)
            => _messenger.SendControlAsync(env, ct);

        public Task<IpcEnvelope?> ReceiveAsync(CancellationToken ct = default)
            => _messenger.ReceiveControlAsync(ct);

        public FramedPipeMessenger Messenger => _messenger;

        public async ValueTask DisposeAsync()
        {
            await _pipe.DisposeAsync();
        }
    }
}

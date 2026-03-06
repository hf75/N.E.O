using System;
using System.Threading;
using System.Threading.Tasks;

namespace Neo.AssemblyForge.Completion;

public sealed class DelegateCompletionProvider : IAssemblyForgeCompletionProvider
{
    private readonly Func<AssemblyForgeCompletionRequest, CancellationToken, Task<string>> _handler;

    public DelegateCompletionProvider(Func<AssemblyForgeCompletionRequest, CancellationToken, Task<string>> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public Task<string> CompleteAsync(AssemblyForgeCompletionRequest request, CancellationToken cancellationToken)
        => _handler(request, cancellationToken);
}

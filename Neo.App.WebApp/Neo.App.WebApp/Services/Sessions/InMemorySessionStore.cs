using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Neo.App.WebApp.Services.Sessions;

/// <summary>
/// In-memory fallback used by the Desktop dev-iteration head and by the unit
/// tests. The Browser head replaces this with an IndexedDB-backed implementation.
/// </summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, NeoSession> _store = new();

    public Task<IReadOnlyList<string>> ListAsync()
        => Task.FromResult<IReadOnlyList<string>>(_store.Keys.OrderBy(k => k).ToList());

    public Task<NeoSession?> LoadAsync(string name)
    {
        _store.TryGetValue(name, out var s);
        return Task.FromResult<NeoSession?>(s);
    }

    public Task SaveAsync(NeoSession session)
    {
        _store[session.Name] = session;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name)
    {
        _store.TryRemove(name, out _);
        return Task.CompletedTask;
    }
}

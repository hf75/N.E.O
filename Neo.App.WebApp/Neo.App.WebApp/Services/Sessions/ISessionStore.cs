using System.Collections.Generic;
using System.Threading.Tasks;

namespace Neo.App.WebApp.Services.Sessions;

/// <summary>
/// Abstraction over the local-persistence backend (IndexedDB in browser,
/// filesystem on the desktop dev-head).
/// </summary>
public interface ISessionStore
{
    Task<IReadOnlyList<string>> ListAsync();
    Task<NeoSession?> LoadAsync(string name);
    Task SaveAsync(NeoSession session);
    Task DeleteAsync(string name);
}

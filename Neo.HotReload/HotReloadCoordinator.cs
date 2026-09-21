using Neo.Agents;
using Neo.IPC;

namespace Neo.HotReload;

/// <param name="Applied">True: the running app now is the new version, do not reload it.</param>
/// <param name="Message">For the log and for the user: what happened, or why a full reload is needed.</param>
public sealed record HotReloadAttempt(bool Applied, string Message);

/// <summary>
/// Everything a host needs for hot reload, for one running app (one plugin window):
///
/// <code>
/// // new code was compiled to newDllPath
/// var attempt = await hotReload.TryAsync(newDllPath, request => child.HotReloadAsync(request));
/// if (!attempt.Applied)
/// {
///     …unload + load newDllPath as before…
///     await hotReload.StartAsync(newDllPath, child.HotReloadCapabilities);
/// }
/// </code>
///
/// Hosts only pass DLL paths around: sources and references of a build are read from the
/// <see cref="HotReloadBaselineFile"/> the compile agent writes next to the DLL. Nothing here
/// throws — hot reload is an optimisation, and every failure ends in "reload it the old way".
/// </summary>
public sealed class HotReloadCoordinator : IDisposable
{
    private readonly HotReloadOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HotReloadSession? _session;
    private HashSet<string> _references = new(StringComparer.OrdinalIgnoreCase);
    private int _starting;

    public HotReloadCoordinator(HotReloadOptions? options = null) => _options = options ?? new HotReloadOptions();

    /// <summary>
    /// Is there a running app we could patch — or is its session still being prepared? (Hosts may
    /// start the session in the background; <see cref="TryAsync"/> waits for it.)
    /// </summary>
    public bool IsActive => _session != null || Volatile.Read(ref _starting) > 0;

    /// <summary>
    /// Call after <paramref name="loadedDllPath"/> was loaded into the plugin window.
    /// </summary>
    /// <param name="childCapabilities">From the window's Hello message; null means that window cannot hot reload.</param>
    /// <returns>Null when hot reload is armed, otherwise why it is not.</returns>
    public async Task<string?> StartAsync(string loadedDllPath, string? childCapabilities, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _starting);
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _starting);
            throw;
        }
        try
        {
            ResetCore();
            if (string.IsNullOrWhiteSpace(childCapabilities))
                return "the plugin window was started without hot reload support";

            var baseline = HotReloadBaseline.TryLoad(loadedDllPath);
            if (baseline == null)
                return "the DLL was not built for hot reload";

            _session = await HotReloadSession.StartAsync(baseline, childCapabilities, _options, cancellationToken).ConfigureAwait(false);
            _references = new HashSet<string>(baseline.ReferencePaths, StringComparer.OrdinalIgnoreCase);
            return null;
        }
        catch (Exception ex)
        {
            ResetCore();
            return "hot reload could not be prepared: " + ex.Message;
        }
        finally
        {
            Interlocked.Decrement(ref _starting);
            _gate.Release();
        }
    }

    /// <summary>
    /// Tries to turn the running app into the build at <paramref name="newDllPath"/> without reloading it.
    /// </summary>
    /// <param name="sendToChild">Sends the request to the plugin window and returns its answer (null: no answer).</param>
    public async Task<HotReloadAttempt> TryAsync(string newDllPath,
        Func<HotReloadRequest, Task<HotReloadResultMessage?>> sendToChild, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session == null)
                return new HotReloadAttempt(false, "no hot reload session for the running app");

            var build = HotReloadBaselineFile.TryRead(newDllPath);
            if (build == null)
                return Abandon("the new build carries no hot reload information");
            if (!_references.SetEquals(build.ReferencePaths))
                return Abandon("the referenced assemblies changed (a package was added or removed)");

            var update = await _session.CreateUpdateAsync(build.SourceTexts, cancellationToken).ConfigureAwait(false);
            switch (update.Status)
            {
                case HotReloadStatus.NoChanges:
                    return new HotReloadAttempt(true, "Hot reload: " + update.Reason + "; the running app was left untouched.");

                case HotReloadStatus.NotPossible:
                    return Abandon(update.Reason ?? "unknown reason");
            }

            var answer = await sendToChild(update.Request!).ConfigureAwait(false);
            if (answer is not { Success: true })
                return Abandon("the plugin window could not apply the update" + (answer?.Error is { } error ? ": " + error : " (no answer)"));

            return new HotReloadAttempt(true,
                $"Hot reload (generation {_session.Generation}): the running app was patched in place, its state is preserved. {answer.Details}");
        }
        catch (Exception ex)
        {
            return Abandon(ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The running app is gone (unloaded, window closed, child restarted).</summary>
    public async Task ResetAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { ResetCore(); }
        finally { _gate.Release(); }
    }

    /// <inheritdoc cref="ResetAsync"/>
    public void Reset()
    {
        _gate.Wait();
        try { ResetCore(); }
        finally { _gate.Release(); }
    }

    // After a failed attempt the running code and the session may disagree; the host reloads
    // fully and starts over.
    private HotReloadAttempt Abandon(string reason)
    {
        ResetCore();
        return new HotReloadAttempt(false, "Hot reload not possible — " + reason + ". Reloading the app.");
    }

    private void ResetCore()
    {
        try { _session?.Dispose(); } catch { /* best effort */ }
        _session = null;
    }

    public void Dispose() => Reset();
}

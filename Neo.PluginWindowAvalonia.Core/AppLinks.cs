using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

using Neo.App;
using Neo.IPC;
using Neo.Shared;

namespace Neo.PluginWindowAvalonia
{
    /// <summary>
    /// The app links of this window. An app link connects two running programs without changing either: a
    /// small communicator, compiled for this link and kept in memory only, runs in the window of the program
    /// that was dropped on. It watches and uses both programs; the window of the other one answers it over
    /// the app network. Over when either program ends, or when someone says so. See App Link in the docs.
    /// </summary>
    internal sealed class AppLinks(MainWindow window, Func<string, string?, Task<string?>> askHost)
    {
        private readonly ConcurrentDictionary<string, AppLink> _links = new();

        internal MainWindow Window => window;

        internal void Tell(string text) => _ = TellAsync(text);

        private async Task TellAsync(string text)
        {
            try { await askHost(LinkMessages.Report, Json.ToJson(new LinkReportMessage(text))); }
            catch (Exception ex) { Debug.WriteLine("[AppLink] " + ex.Message); }
        }

        internal void Forget(AppLink link) => _links.TryRemove(link.Id, out _);

        /// <summary>The host says: run this communicator here.</summary>
        public async Task<LinkStartedMessage> StartAsync(LinkStartRequest request)
        {
            var link = new AppLink(this, request.LinkId, request.PeerName, request.PeerProcessId);
            _links[link.Id] = link;
            await link.ShowAsync();
            return await link.RunCommunicatorAsync(Convert.FromBase64String(request.Assembly), request.Blocks, request.OwnName);
        }

        /// <summary>What the window of the other program says.</summary>
        public async Task<string?> HandleAsync(string kind, string? payload)
        {
            if (payload == null) return null;

            switch (kind)
            {
                case LinkMessages.Start:
                {
                    var start = Json.FromJson<LinkStartMessage>(payload)!;
                    var link = new AppLink(this, start.LinkId, start.RunnerName, start.RunnerProcessId);
                    _links[link.Id] = link;
                    await link.ShowAsync();
                    return "ok";
                }
                case LinkMessages.Watch:
                {
                    var watch = Json.FromJson<LinkWatchMessage>(payload)!;
                    return _links.TryGetValue(watch.LinkId, out var link) ? await link.WatchForTheOtherAsync(watch) : "The link is over.";
                }
                case LinkMessages.Changed:
                {
                    var changed = Json.FromJson<LinkChangedMessage>(payload)!;
                    if (_links.TryGetValue(changed.LinkId, out var link)) link.Changed(changed);
                    return "ok";
                }
                case LinkMessages.Do:
                {
                    var todo = Json.FromJson<LinkDoMessage>(payload)!;
                    return _links.TryGetValue(todo.LinkId, out var link) ? await link.DoAsync(todo) : "The link is over.";
                }
                case LinkMessages.End:
                {
                    var end = Json.FromJson<LinkEndMessage>(payload)!;
                    if (_links.TryGetValue(end.LinkId, out var link)) link.End(end.Reason, tellTheOther: false);
                    return "ok";
                }
                default:
                    return null;
            }
        }
    }

    /// <summary>One app link, in the window of one of its two programs.</summary>
    internal sealed class AppLink
    {
        private const int WatchEveryMs = 50;
        private const int MaxProblems = 8;
        private static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(15);

        private readonly AppLinks _owner;
        private readonly string _peerName;
        private readonly int _peerProcess;
        private readonly CancellationTokenSource _over = new();

        // One after the other each: what the other side must do, what this side must do, what the communicator wants to hear.
        private readonly LinkOutbox _toTheOther;
        private readonly LinkOutbox _here;
        private readonly LinkOutbox _callbacks;

        // On the UI thread.
        private readonly List<LinkWatcher> _watchers = [];
        private DispatcherTimer? _timer;
        private (Control? Control, string? What) _doing;

        private readonly ConcurrentDictionary<int, (bool IsClick, Action<string?> Then)> _waitingFor = new();
        private readonly List<string> _problems = [];
        private AppDescriptor? _peer;
        private AssemblyLoadContext? _communicator;
        private int _nextWatch;
        private int _ended;

        public AppLink(AppLinks owner, string id, string peerName, int peerProcess)
        {
            _owner = owner;
            Id = id;
            _peerName = peerName;
            _peerProcess = peerProcess;
            _toTheOther = new LinkOutbox(ex => Problem(ex.Message));
            _here = new LinkOutbox(ex => Problem(ex.Message));
            _callbacks = new LinkOutbox(ex => Problem(ex.Message));
            _ = EndWithThePeerAsync();
        }

        public string Id { get; }

        private MainWindow Window => _owner.Window;

        /// <summary>The chip on the window that says the link is there, and takes it away.</summary>
        public Task ShowAsync() => Dispatcher.UIThread.InvokeAsync(() =>
        {
            Window.ShowLinkChip(Id, $"Linked to {_peerName}", () => End(null, tellTheOther: true));
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(WatchEveryMs) };
            _timer.Tick += (_, _) =>
            {
                foreach (var watcher in _watchers.ToArray()) watcher.Tick();
            };
            _timer.Start();
        }).GetTask();

        private async Task EndWithThePeerAsync()
        {
            try
            {
                using var process = Process.GetProcessById(_peerProcess);
                await process.WaitForExitAsync(_over.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (ArgumentException) { /* it is gone already */ }
            End($"{_peerName} was closed. The link is gone.", tellTheOther: false);
        }

        // ── The communicator ─────────────────────────────────────────────

        public async Task<LinkStartedMessage> RunCommunicatorAsync(byte[] assembly, IReadOnlyList<int> blocks, string ownName)
        {
            try
            {
                await SendAsync(LinkMessages.Start, Json.ToJson(new LinkStartMessage(Id, ownName, Environment.ProcessId)));

                _communicator = new AssemblyLoadContext("App link " + Id, isCollectible: true);
                using var bytes = new MemoryStream(assembly);
                var type = _communicator.LoadFromStream(bytes).GetType("LinkCommunicator")
                    ?? throw new InvalidOperationException("The communicator has no class LinkCommunicator.");

                ILinkSide a = new OtherSide(this, _peerName), b = new OwnSide(this, ownName);
                foreach (var block in blocks)
                {
                    var method = type.GetMethod("Block" + block) ?? throw new InvalidOperationException($"The communicator has no block {block}.");
                    _callbacks.Post(null, () =>
                    {
                        try { method.Invoke(null, [a, b]); }
                        catch (TargetInvocationException ex) { Problem(ex.InnerException?.Message ?? ex.Message); }
                        return Task.CompletedTask;
                    });
                }

                // What the blocks asked for has been tried when these are done, and what failed has said so.
                await _callbacks.FlushAsync();
                await _here.FlushAsync();
                await _toTheOther.FlushAsync();

                List<string> problems;
                lock (_problems) problems = [.. _problems];
                return new LinkStartedMessage(true, problems);
            }
            catch (Exception ex) when (ex is IOException or AppCallException or AppNotFoundException or TimeoutException or OperationCanceledException or BadImageFormatException or InvalidOperationException)
            {
                End($"The link to {_peerName} could not be made: {ex.Message}", tellTheOther: true);
                return new LinkStartedMessage(false, [ex.Message]);
            }
        }

        // The program this window runs.
        private sealed class OwnSide(AppLink link, string name) : ILinkSide
        {
            public string Name => name;

            public void OnChange<T>(string control, string property, Action<T> then) =>
                link.WatchHere(LinkWatchKind.Property, control, property, link.Calling(then));

            public void OnField<T>(string field, Action<T> then) =>
                link.WatchHere(LinkWatchKind.Field, field, null, link.Calling(then));

            public void OnClick(string button, Action then) =>
                link.WatchHere(LinkWatchKind.Click, button, null, _ => then());

            public void Set(string control, string property, object? value) =>
                link.DoHere(new LinkDoMessage(link.Id, LinkOperation.Set, control, property, [LinkValue.ToJson(value)]));

            public void SetField(string field, object? value) =>
                link.DoHere(new LinkDoMessage(link.Id, LinkOperation.SetField, field, null, [LinkValue.ToJson(value)]));

            public void Press(string button) =>
                link.DoHere(new LinkDoMessage(link.Id, LinkOperation.Press, button, null, []));

            public void Call(string method, params object?[] args) =>
                link.DoHere(new LinkDoMessage(link.Id, LinkOperation.Call, method, null, args.Select(LinkValue.ToJson).ToList()));
        }

        // The program of the other window.
        private sealed class OtherSide(AppLink link, string name) : ILinkSide
        {
            public string Name => name;

            public void OnChange<T>(string control, string property, Action<T> then) =>
                link.WatchThere(LinkWatchKind.Property, control, property, link.Calling(then));

            public void OnField<T>(string field, Action<T> then) =>
                link.WatchThere(LinkWatchKind.Field, field, null, link.Calling(then));

            public void OnClick(string button, Action then) =>
                link.WatchThere(LinkWatchKind.Click, button, null, _ => then());

            public void Set(string control, string property, object? value) =>
                link.DoThere(new LinkDoMessage(link.Id, LinkOperation.Set, control, property, [LinkValue.ToJson(value)]));

            public void SetField(string field, object? value) =>
                link.DoThere(new LinkDoMessage(link.Id, LinkOperation.SetField, field, null, [LinkValue.ToJson(value)]));

            public void Press(string button) =>
                link.DoThere(new LinkDoMessage(link.Id, LinkOperation.Press, button, null, []));

            public void Call(string method, params object?[] args) =>
                link.DoThere(new LinkDoMessage(link.Id, LinkOperation.Call, method, null, args.Select(LinkValue.ToJson).ToList()));
        }

        private Action<string?> Calling<T>(Action<T> then) => json => then(LinkValue.Convert<T>(json!));

        private void WatchHere(LinkWatchKind kind, string address, string? property, Action<string?> then)
        {
            var id = Interlocked.Increment(ref _nextWatch);
            var key = kind == LinkWatchKind.Click ? null : "watch" + id;
            _here.Post(null, async () =>
            {
                var problem = await Dispatcher.UIThread.InvokeAsync(() =>
                    NewWatcher(kind, address, property, json => _callbacks.Post(key, () => { then(json); return Task.CompletedTask; })));
                if (problem != null) Problem(problem);
            });
        }

        private void WatchThere(LinkWatchKind kind, string address, string? property, Action<string?> then)
        {
            var id = Interlocked.Increment(ref _nextWatch);
            _waitingFor[id] = (kind == LinkWatchKind.Click, then);
            _toTheOther.Post(null, async () =>
            {
                var answer = await SendAsync(LinkMessages.Watch, Json.ToJson(new LinkWatchMessage(Id, id, kind, address, property)));
                if (answer != "ok") Problem($"{_peerName}: {answer}");
            });
        }

        private void DoHere(LinkDoMessage todo) =>
            _here.Post(KeyOf(todo), async () =>
            {
                var answer = await DoAsync(todo);
                if (answer != "ok") Problem(answer);
            });

        private void DoThere(LinkDoMessage todo) =>
            _toTheOther.Post(KeyOf(todo), async () =>
            {
                var answer = await SendAsync(LinkMessages.Do, Json.ToJson(todo));
                if (answer != "ok") Problem($"{_peerName}: {answer}");
            });

        // The latest value is what counts; a press or a call happens every time.
        private static string? KeyOf(LinkDoMessage todo) => todo.Operation switch
        {
            LinkOperation.Set => $"set {todo.Address} {todo.Property}",
            LinkOperation.SetField => $"field {todo.Address}",
            _ => null,
        };

        /// <summary>A change in the program of the other window, for the communicator.</summary>
        public void Changed(LinkChangedMessage changed)
        {
            if (!_waitingFor.TryGetValue(changed.WatchId, out var waiting)) return;
            _callbacks.Post(waiting.IsClick ? null : "watch" + changed.WatchId, () => { waiting.Then(changed.Value); return Task.CompletedTask; });
        }

        // ── What this window does for the link ───────────────────────────

        /// <summary>The window that runs the communicator wants to hear about a change here.</summary>
        public async Task<string> WatchForTheOtherAsync(LinkWatchMessage watch)
        {
            var key = watch.Kind == LinkWatchKind.Click ? null : "watch" + watch.WatchId;
            var problem = await Dispatcher.UIThread.InvokeAsync(() => NewWatcher(watch.Kind, watch.Address, watch.Property,
                json => _toTheOther.Post(key, () => SendAsync(LinkMessages.Changed, Json.ToJson(new LinkChangedMessage(Id, watch.WatchId, json))))));
            return problem ?? "ok";
        }

        private string? NewWatcher(LinkWatchKind kind, string address, string? property, Action<string?> changed)
        {
            var watcher = new LinkWatcher(Window, kind, address, property,
                (control, what) => ReferenceEquals(_doing.Control, control) && _doing.What == what,
                changed, Problem);
            _watchers.Add(watcher);
            return watcher.Begin();
        }

        /// <summary>Does something to this program, on its UI thread. "ok" or what went wrong.</summary>
        public async Task<string> DoAsync(LinkDoMessage todo)
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => Do(todo));
                return "ok";
            }
            catch (Exception ex)
            {
                return (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message;
            }
        }

        private void Do(LinkDoMessage todo)
        {
            var root = Window.GetLoadedUserControl() ?? throw new InvalidOperationException("The program is not loaded right now.");

            switch (todo.Operation)
            {
                case LinkOperation.Set:
                    SetProperty(root, todo.Address, todo.Property ?? "", todo.Values[0]);
                    break;

                case LinkOperation.SetField:
                    SetField(root, todo.Address, todo.Values[0]);
                    foreach (var watcher in _watchers) watcher.Expect(todo.Address, todo.Values[0]);
                    break;

                case LinkOperation.Press:
                    Press(root, todo.Address);
                    break;

                case LinkOperation.Call:
                    Call(root, todo.Address, todo.Values);
                    break;
            }
        }

        private void SetProperty(Control root, string address, string property, string json)
        {
            var control = LinkControls.Find(root, address) ?? throw new InvalidOperationException($"There is no control {address}.");
            var avalonia = LinkControls.PropertyOf(control, property);
            var clr = avalonia == null ? LinkControls.ClrPropertyOf(control, property) : null;
            if (avalonia == null && clr == null)
                throw new InvalidOperationException($"{address} ({control.GetType().Name}) has no property {property}.");

            var value = App.ToPropertyValue(json, avalonia?.PropertyType ?? clr!.PropertyType);
            _doing = (control, avalonia?.Name ?? clr!.Name);
            try
            {
                if (avalonia != null) control.SetValue(avalonia, value);
                else clr!.SetValue(control, value);
            }
            finally
            {
                _doing = default;
            }
        }

        private static void SetField(Control root, string name, string json)
        {
            var type = root.GetType();
            var field = AppStateImage.FieldOf(type, name);
            if (field != null)
            {
                field.SetValue(field.IsStatic ? null : root, LinkValue.Convert(json, field.FieldType));
                return;
            }
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                ?? throw new InvalidOperationException($"The program has no field {name}.");
            property.SetValue(root, LinkValue.Convert(json, property.PropertyType));
        }

        private void Press(Control root, string address)
        {
            if (LinkControls.Find(root, address) is not Button button)
                throw new InvalidOperationException($"There is no button {address}.");
            if (!button.IsEffectivelyEnabled) return; // a person could not press it either

            _doing = (button, "Click");
            try
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (button.Command is { } command && command.CanExecute(button.CommandParameter))
                    command.Execute(button.CommandParameter);
            }
            finally
            {
                _doing = default;
            }
        }

        private void Call(Control root, string name, IReadOnlyList<string> values)
        {
            var method = MethodOf(root.GetType(), name, values.Count) ?? throw new InvalidOperationException($"The program has no method {name} that takes {values.Count} value(s).");
            var parameters = method.GetParameters();
            var args = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
                args[i] = i < values.Count ? LinkValue.Convert(values[i], parameters[i].ParameterType) : parameters[i].DefaultValue;

            if (method.Invoke(method.IsStatic ? null : root, args) is Task running)
                _ = running.ContinueWith(t => Problem(t.Exception?.GetBaseException().Message ?? "It failed."), TaskContinuationOptions.OnlyOnFaulted);
        }

        private static MethodInfo? MethodOf(Type type, string name, int given)
        {
            var plugin = type.Assembly;
            for (var t = type; t != null && t.Assembly == plugin; t = t.BaseType)
            {
                var found = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == name && !m.IsGenericMethod)
                    .FirstOrDefault(m => given <= m.GetParameters().Length && m.GetParameters().Skip(given).All(p => p.HasDefaultValue));
                if (found != null) return found;
            }
            return null;
        }

        // ── Talking to the other window ──────────────────────────────────

        // Its address changes when its app gets replaced; its process does not. Meanwhile it just takes a moment.
        private async Task<string> SendAsync(string kind, string payload)
        {
            Exception? last = null;
            for (int attempt = 0; attempt < 12 && !_over.IsCancellationRequested; attempt++)
            {
                var peer = _peer ??= Apps.Running().FirstOrDefault(a => a.ProcessId == _peerProcess);
                if (peer != null)
                {
                    try
                    {
                        return await AppNetwork.SendSystemAsync(peer, kind, payload, AnswerWithin) ?? "ok";
                    }
                    catch (Exception ex) when (ex is IOException or AppCallException or AppNotFoundException or TimeoutException or UnauthorizedAccessException)
                    {
                        last = ex;
                        _peer = null;
                    }
                }
                await Task.Delay(250);
            }
            throw new InvalidOperationException($"{_peerName} does not answer" + (last == null ? "." : $": {last.Message}"));
        }

        // ── When something goes wrong, and at the end ────────────────────

        private void Problem(string text)
        {
            lock (_problems)
            {
                if (_problems.Contains(text) || _problems.Count >= MaxProblems) return;
                _problems.Add(text);
            }
            _owner.Tell($"Link to {_peerName}: {text}");
        }

        /// <param name="reason">Why, for the person; null: the person ended it.</param>
        public void End(string? reason, bool tellTheOther)
        {
            if (Interlocked.Exchange(ref _ended, 1) == 1) return;

            if (tellTheOther) _ = TellTheOtherItIsOverAsync(reason);
            _over.Cancel();
            _toTheOther.Close();
            _here.Close();
            _callbacks.Close();
            _owner.Forget(this);
            _waitingFor.Clear();

            var text = reason ?? $"The link to {_peerName} is gone.";
            _owner.Tell(text);
            Dispatcher.UIThread.Post(() =>
            {
                _timer?.Stop();
                foreach (var watcher in _watchers) watcher.Stop();
                _watchers.Clear();
                Window.RemoveLinkChip(Id);
                Window.ShowBanner(text, "#D0202020", 5);
            });

            _communicator?.Unload();
            _communicator = null;
        }

        private async Task TellTheOtherItIsOverAsync(string? reason)
        {
            try
            {
                if ((_peer ?? Apps.Running().FirstOrDefault(a => a.ProcessId == _peerProcess)) is { } peer)
                    await AppNetwork.SendSystemAsync(peer, LinkMessages.End, Json.ToJson(new LinkEndMessage(Id, reason)), AnswerWithin);
            }
            catch (Exception ex) when (ex is IOException or AppCallException or AppNotFoundException or TimeoutException or UnauthorizedAccessException)
            {
                Debug.WriteLine("[AppLink] " + ex.Message);
            }
        }
    }
}

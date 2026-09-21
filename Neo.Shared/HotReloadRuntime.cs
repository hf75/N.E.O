using System.Collections;
using System.Reflection;
using System.Reflection.Metadata;
using Neo.IPC;

namespace Neo.Shared
{
    /// <summary>
    /// The plugin window's half of hot reload, free of any UI toolkit: patch the loaded plugin
    /// assembly in place (<see cref="MetadataUpdater.ApplyUpdate"/>) and give the objects that
    /// are already alive what constructors and initializers would have given them. The windows
    /// add the toolkit-specific part around it: carry the UI state over and rebuild the visual tree.
    ///
    /// <para>Only works when the process was started with <c>DOTNET_MODIFIABLE_ASSEMBLIES=debug</c>
    /// (<see cref="EnvironmentVariable"/>) — the hosts set it when they launch a plugin window —
    /// and for plugins that were compiled without optimizations.</para>
    /// </summary>
    public static class HotReloadRuntime
    {
        /// <summary>Has to be in the environment before the runtime starts; it cannot be switched on later.</summary>
        public const string EnvironmentVariable = "DOTNET_MODIFIABLE_ASSEMBLIES";
        public const string EnvironmentValue = "debug";

        public static bool IsSupported => MetadataUpdater.IsSupported;

        /// <summary>
        /// What this runtime can patch ("Baseline AddMethodToExistingType …"), or null without hot
        /// reload. Roslyn needs the list to decide which edits are possible. The runtime only tells
        /// its own hot reload agent, hence the reflection; "Baseline" (method bodies) is always safe.
        /// </summary>
        public static string? Capabilities
        {
            get
            {
                if (!IsSupported) return null;
                try
                {
                    var value = typeof(MetadataUpdater)
                        .GetMethod("GetCapabilities", BindingFlags.NonPublic | BindingFlags.Static)?
                        .Invoke(null, null) as string;
                    return string.IsNullOrWhiteSpace(value) ? "Baseline" : value;
                }
                catch { return "Baseline"; }
            }
        }

        /// <summary>Adds what a plugin window needs in its environment to be able to hot reload.</summary>
        public static void ConfigureChildEnvironment(IDictionary<string, string?> environment)
            => environment[EnvironmentVariable] = EnvironmentValue;

        public sealed record ApplyResult(int Generations, int ObjectsUpdated, int FieldsInitialised, int HelpersRun)
        {
            public override string ToString()
                => $"{Generations} generation(s) applied" +
                   (FieldsInitialised > 0 ? $", {FieldsInitialised} field(s) initialised on {ObjectsUpdated} live object(s)" : "") +
                   (HelpersRun > 0 ? $", {HelpersRun} helper(s) run" : "");
        }

        /// <summary>
        /// Applies the deltas and runs the state steps. Must run on the UI thread: the steps touch
        /// the live app. Throws when the runtime rejects an update — the plugin may then be
        /// half-patched, so the caller has to report failure and the host reloads it.
        /// </summary>
        /// <param name="root">The loaded control; live objects are found by walking its object graph.</param>
        public static ApplyResult Apply(Assembly plugin, object? root, HotReloadRequest request)
        {
            foreach (var delta in request.Deltas)
            {
                MetadataUpdater.ApplyUpdate(plugin,
                    Convert.FromBase64String(delta.MetadataDelta),
                    Convert.FromBase64String(delta.IlDelta),
                    Convert.FromBase64String(delta.PdbDelta));
            }

            InvokeMetadataUpdateHandlers();

            int objects = 0, fields = 0, helpers = 0;
            foreach (var step in request.StateSteps ?? [])
            {
                var type = plugin.GetType(step.TypeName, throwOnError: false);
                var helper = type?.GetMethod(step.MethodName,
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (type == null || helper == null)
                    throw new MissingMethodException(step.TypeName, step.MethodName);

                if (step.IsStatic)
                {
                    helper.Invoke(null, null);
                    helpers++;
                    continue;
                }

                foreach (var instance in root == null ? [] : FindLiveObjects(root, plugin, type))
                {
                    if (step.FieldNames.Count == 0)
                    {
                        helper.Invoke(instance, null); // statements that were added to the constructor
                        helpers++;
                        continue;
                    }

                    // Every object gets its own values: "new List<string>()" must not be shared.
                    var values = (object?[])helper.Invoke(null, null)!;
                    for (int i = 0; i < step.FieldNames.Count && i < values.Length; i++)
                    {
                        var field = type.GetField(step.FieldNames[i],
                            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                        if (field == null) continue;
                        field.SetValue(instance, values[i]);
                        fields++;
                    }
                    objects++;
                }
            }

            return new ApplyResult(request.Deltas.Count, objects, fields, helpers);
        }

        /// <param name="NewRoot">What the build method returned — apps written as <c>Content = BuildUI();</c> hand the new root back instead of assigning it themselves.</param>
        /// <param name="StoppedTimers">Timers the build method replaced (see <see cref="Rebuild"/>).</param>
        /// <param name="RemovedSubscriptions">Old event subscriptions the build method made again (see <see cref="EventSubscriptionSnapshot"/>).</param>
        public sealed record RebuildResult(object? NewRoot, int StoppedTimers, int RemovedSubscriptions)
        {
            /// <summary>", 1 replaced timer(s) stopped, 2 duplicate event subscription(s) removed" — empty when there was nothing to clean up.</summary>
            public string Cleanup
                => (StoppedTimers > 0 ? $", {StoppedTimers} replaced timer(s) stopped" : "") +
                   (RemovedSubscriptions > 0 ? $", {RemovedSubscriptions} duplicate event subscription(s) removed" : "");
        }

        /// <summary>Calls the app's UI build method again and returns what it returned.</summary>
        public static object? InvokeRebuild(object root, string methodName) => Rebuild(root, methodName).NewRoot;

        /// <summary>
        /// Calls the app's UI build method again and cleans up after it. Building twice leaves
        /// behind what only building once gets right:
        /// <list type="bullet">
        ///   <item>Timers. Apps often start a timer while building the UI and keep it in a field;
        ///         building again stores a new timer there, and the old one — still enabled, still
        ///         rooted by its dispatcher or thread pool — would keep ticking alongside it. A timer
        ///         field that holds a different object afterwards has its old timer stopped and disposed.</item>
        ///   <item>Event subscriptions to objects that outlive the rebuild: the old one of each pair
        ///         is removed (<see cref="EventSubscriptionSnapshot"/>).</item>
        /// </list>
        /// </summary>
        /// <param name="routedEvents">The toolkit's routed-event handlers; null covers plain .NET events only.</param>
        public static RebuildResult Rebuild(object root, string methodName, IRoutedEventHandlers? routedEvents = null)
        {
            var method = FindParameterless(root.GetType(), methodName)
                ?? throw new MissingMethodException(root.GetType().FullName, methodName);

            var timersBefore = TimerFields(root).Select(f => (Field: f, Timer: f.GetValue(root))).Where(t => t.Timer != null).ToList();
            var subscriptions = EventSubscriptionSnapshot.Capture(root, routedEvents);

            var result = method.Invoke(root, null);

            int stoppedTimers = 0;
            foreach (var (field, oldTimer) in timersBefore)
            {
                if (ReferenceEquals(field.GetValue(root), oldTimer)) continue;
                try
                {
                    oldTimer!.GetType().GetMethod("Stop", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)?.Invoke(oldTimer, null);
                    (oldTimer as IDisposable)?.Dispose();
                    stoppedTimers++;
                }
                catch { /* a timer that cannot be stopped keeps running, as before */ }
            }

            return new RebuildResult(result, stoppedTimers, subscriptions.RemoveReplaced());
        }

        private static IEnumerable<FieldInfo> TimerFields(object root)
        {
            var plugin = root.GetType().Assembly;
            for (var t = root.GetType(); t != null && t.Assembly == plugin; t = t.BaseType)
            {
                foreach (var field in t.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (IsTimer(field.FieldType)) yield return field;
            }
        }

        // DispatcherTimer (Avalonia, WPF), System.Timers.Timer, System.Threading.Timer, PeriodicTimer, …
        private static bool IsTimer(Type type)
        {
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                if (t.Name.EndsWith("Timer", StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Optional hook: an app that wants to know it was hot reloaded declares <c>void OnHotReloaded()</c>.</summary>
        public static void InvokeOnHotReloaded(object root)
        {
            try { FindParameterless(root.GetType(), "OnHotReloaded")?.Invoke(root, null); }
            catch { /* the app's own business */ }
        }

        /// <summary>
        /// Thrown by code Roslyn had to retire during an update (a lambda that no longer exists).
        /// Such an exception means "this delegate is stale", not "the app is broken".
        /// </summary>
        public static bool IsStaleCodeException(Exception? exception)
        {
            for (var e = exception; e != null; e = e.InnerException)
                if (e.GetType().FullName == "System.Runtime.CompilerServices.HotReloadException") return true;
            return false;
        }

        private static MethodInfo? FindParameterless(Type type, string name)
        {
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                var method = t.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.GetParameters().Length == 0 && !m.IsGenericMethodDefinition &&
                                         string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
                if (method != null) return method;
            }
            return null;
        }

        /// <summary>
        /// What the hot reload agent of dotnet-watch does after an update: every assembly may name
        /// handlers that drop caches built from the old metadata (the runtime's own reflection
        /// cache is one of them — without this, members added by the update stay invisible).
        /// </summary>
        private static void InvokeMetadataUpdateHandlers()
        {
            var handlers = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var attribute in assembly.GetCustomAttributesData())
                    {
                        if (attribute.AttributeType.FullName == "System.Reflection.Metadata.MetadataUpdateHandlerAttribute" &&
                            attribute.ConstructorArguments.Count == 1 && attribute.ConstructorArguments[0].Value is Type handler)
                            handlers.Add(handler);
                    }
                }
                catch { /* dynamic or broken assembly */ }
            }

            foreach (var name in new[] { "ClearCache", "UpdateApplication" })
            {
                foreach (var handler in handlers)
                {
                    try
                    {
                        handler.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(Type[])])
                            ?.Invoke(null, [null]);
                    }
                    catch { /* a handler that fails must not fail the update */ }
                }
            }
        }

        /// <summary>
        /// Live objects of <paramref name="target"/> reachable from the loaded control. Follows the
        /// fields of the plugin's own types and the elements of collections; never walks into the
        /// UI toolkit or other foreign objects.
        /// </summary>
        internal static List<object> FindLiveObjects(object root, Assembly plugin, Type target)
        {
            const int limit = 250_000;
            var found = new List<object>();
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var pending = new Queue<object>();
            pending.Enqueue(root);

            while (pending.Count > 0 && visited.Count < limit)
            {
                var current = pending.Dequeue();
                if (!visited.Add(current)) continue;

                var type = current.GetType();
                if (target.IsInstanceOfType(current)) found.Add(current);

                if (type.Assembly == plugin)
                {
                    for (var t = type; t != null && t.Assembly == plugin; t = t.BaseType)
                    {
                        foreach (var field in t.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        {
                            if (field.FieldType.IsPrimitive || field.FieldType == typeof(string) || field.FieldType.IsPointer) continue;
                            object? value;
                            try { value = field.GetValue(current); } catch { continue; }
                            if (value != null && IsWorthVisiting(value, plugin)) pending.Enqueue(value);
                        }
                    }
                }
                else if (current is IDictionary dictionary)
                {
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (IsWorthVisiting(entry.Key, plugin)) pending.Enqueue(entry.Key);
                        if (entry.Value != null && IsWorthVisiting(entry.Value, plugin)) pending.Enqueue(entry.Value);
                    }
                }
                else if (IsMaterializedCollection(current))
                {
                    foreach (var item in (IEnumerable)current)
                        if (item != null && IsWorthVisiting(item, plugin)) pending.Enqueue(item);
                }
            }
            return found;
        }

        private static bool IsWorthVisiting(object value, Assembly plugin)
            => value.GetType().Assembly == plugin || value is IDictionary || IsMaterializedCollection(value);

        // Collections that hold their elements (lists, arrays, sets, ObservableCollection) — not
        // lazy sequences, whose enumeration could run queries or have side effects.
        private static bool IsMaterializedCollection(object value)
        {
            if (value is string || value is not IEnumerable) return false;
            if (value is ICollection) return true;
            return value.GetType().GetInterfaces().Any(i => i.IsGenericType &&
                (i.GetGenericTypeDefinition() == typeof(ICollection<>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>)));
        }
    }
}

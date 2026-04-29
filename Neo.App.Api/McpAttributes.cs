namespace Neo.App;

/// <summary>
/// Marks a method as callable by Claude via Live-MCP.
///
/// Live-MCP is the Phase 1+ extension where each generated Neo app exposes a
/// machine-readable manifest of methods, observables, and triggerables. Claude
/// can then drive the app through MCP tools (<c>invoke_method</c>,
/// <c>read_observable</c>, etc.).
///
/// <para>Without this attribute, a method is invisible to Claude — opt-in by design.</para>
///
/// Example:
/// <code>
///   [McpCallable("Filters the product list by category and minimum price.")]
///   public void ApplyFilter(string category, decimal minPrice) { … }
///
///   [McpCallable("Reloads data fresh from the API.", OffUiThread = true, TimeoutSeconds = 60)]
///   public async Task&lt;int&gt; RefreshFromApi() { … }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class McpCallableAttribute : Attribute
{
    /// <summary>Human-readable description shown to Claude in the tool/manifest listing.</summary>
    public string Description { get; }

    /// <summary>
    /// If <c>true</c>, the method is invoked off the UI thread. Defaults to <c>false</c>
    /// (UI-thread dispatch) which is safe for methods that read/modify Avalonia controls.
    /// </summary>
    public bool OffUiThread { get; init; }

    /// <summary>Per-invocation timeout. Default 30 s. Set higher for long-running tasks.</summary>
    public int TimeoutSeconds { get; init; } = 30;

    public McpCallableAttribute(string description) => Description = description;
}

/// <summary>
/// Marks a property as readable state for Claude via <c>read_observable</c>.
///
/// Set <see cref="Watchable"/> to <c>true</c> to expose the property as an MCP resource
/// (URI <c>app://&lt;appId&gt;/&lt;propertyName&gt;</c>) so Claude can subscribe to changes
/// via <c>resources/subscribe</c> instead of polling.
///
/// Example:
/// <code>
///   [McpObservable("Number of products currently visible after filtering.")]
///   public int VisibleProductCount =&gt; _filtered.Count;
///
///   [McpObservable("Currently active filter category.", Watchable = true)]
///   public string CurrentCategory { get; private set; }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class McpObservableAttribute : Attribute
{
    /// <summary>Human-readable description shown to Claude in the manifest listing.</summary>
    public string Description { get; }

    /// <summary>
    /// If <c>true</c>, the property is published as an MCP resource that Claude can
    /// subscribe to. Property changes flow as <c>notifications/resources/updated</c>
    /// (with a small server-side coalesce window for rapid-fire changes).
    /// Requires either <see cref="System.ComponentModel.INotifyPropertyChanged"/>
    /// on the host control or falls back to periodic polling.
    /// </summary>
    public bool Watchable { get; init; }

    public McpObservableAttribute(string description) => Description = description;
}

/// <summary>
/// Marks a control-returning property as a triggerable surface point — Claude can
/// fire its primary user interaction (e.g. <c>Click</c> on a Button) via
/// <c>invoke_method</c>'s trigger pathway or via simulated input in Phase 3.
///
/// Example:
/// <code>
///   [McpTriggerable("Reloads the dashboard.")]
///   public Avalonia.Controls.Button RefreshButton =&gt; _refreshButton;
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class McpTriggerableAttribute : Attribute
{
    /// <summary>Human-readable description of what the trigger does.</summary>
    public string Description { get; }

    public McpTriggerableAttribute(string description) => Description = description;
}

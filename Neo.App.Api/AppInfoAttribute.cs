namespace Neo.App;

/// <summary>
/// Names the app on the app network (see <see cref="Apps"/>), so that other N.E.O. apps on this
/// machine can find it by that name. Without it the app is known by the name its host gives it
/// (the project or window title).
///
/// Example:
/// <code>
///   [AppInfo("Sales", "Records sales and knows the daily total.")]
///   public class DynamicUserControl : UserControl { … }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class AppInfoAttribute : Attribute
{
    /// <summary>The name other apps use to reach this one.</summary>
    public string Name { get; }

    /// <summary>What the app is for — shown to the AI when another app is asked to use this one.</summary>
    public string Description { get; }

    /// <summary>
    /// The other program this app is the interface of: its process name, or a part of its window title, as
    /// <c>ForeignApp.Attach</c> takes it. Other apps and Claude then use that program through this app's methods,
    /// a drop on that program's window links with this app, and a call that finds the program changed waits while
    /// N.E.O. repairs the interface.
    /// </summary>
    public string? Program { get; init; }

    public AppInfoAttribute(string name, string description = "")
    {
        Name = name;
        Description = description;
    }
}

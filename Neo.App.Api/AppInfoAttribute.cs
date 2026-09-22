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

    public AppInfoAttribute(string name, string description = "")
    {
        Name = name;
        Description = description;
    }
}

namespace Neo.ErrorSurfacing;

/// <summary>
/// Where an exception nobody caught ends up in an exported app, which has no N.E.O. behind it to
/// repair it: in a window, and the app goes on — the exceptions <see cref="LostExceptions"/> brings
/// to the UI thread among them. One window at a time, and an error the user has seen is not shown
/// again (a timer that throws on every tick would bring the window back at once).
///
/// <para>An export program puts the source for its UI framework into its namespace, calls
/// <c>NeoErrorWindow.Install()</c> on the UI thread before the app runs and creates its main window
/// with <c>NeoErrorWindow.MainWindow(() => …)</c>: if that throws, the error takes the main window's
/// place, and closing it ends the app.</para>
/// </summary>
public static class ExportErrors
{
    /// <summary><c>NeoErrorWindow</c> for an exported Avalonia app (Runtime/NeoErrorWindow.Avalonia.cs).</summary>
    public static string AvaloniaSource { get; } = EmbeddedSource.Read("NeoErrorWindow.Avalonia.cs");

    /// <summary><c>NeoErrorWindow</c> for an exported WPF app (Runtime/NeoErrorWindow.Wpf.cs).</summary>
    public static string WpfSource { get; } = EmbeddedSource.Read("NeoErrorWindow.Wpf.cs");
}

namespace Neo.Tests;

/// <summary>
/// Only one test project at a time shows real windows. <c>dotnet test neo.sln</c> runs the projects side by side,
/// but these tests need the foreground, and a window of one project can take it from the other or come to lie over
/// what it is about to click: a drag let go over a window that has just appeared does nothing at all. Held by a
/// file, so that a test run that dies gives up its turn with it. Used as a collection fixture by both projects.
/// </summary>
public sealed class ForegroundTurn : IDisposable
{
    private readonly FileStream _turn;

    public ForegroundTurn()
    {
        var path = Path.Combine(Path.GetTempPath(), "neo_window_tests.lock");
        while (true)
        {
            try
            {
                _turn = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(500); // the other project has its turn
            }
        }
    }

    public void Dispose() => _turn.Dispose();
}

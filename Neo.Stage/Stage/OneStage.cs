namespace Neo.Stage;

/// <summary>
/// One live stage at a time: two would both listen to the key, and both would answer aloud at once. The newest one wins:
/// it asks the one before it to end and waits until it has.
/// </summary>
internal static class OneStage
{
    private const string LiveName = @"Local\Neo.Stage.Live", QuitName = @"Local\Neo.Stage.Quit";
    private static Mutex? s_live;

    /// <summary>Takes the stage. <paramref name="quit"/> runs, on a thread of its own, when a newer stage asks for it.</summary>
    public static bool Take(Action quit)
    {
        var quitting = new EventWaitHandle(false, EventResetMode.AutoReset, QuitName);
        s_live = new Mutex(false, LiveName);
        try
        {
            if (!s_live.WaitOne(0))
            {
                App.Log("another stage is running; asking it to end");
                quitting.Set();
                if (!s_live.WaitOne(TimeSpan.FromSeconds(8)))
                {
                    quitting.Reset(); // it did not listen; the request must not end a later one
                    return false;
                }
            }
        }
        catch (AbandonedMutexException)
        {
            // The one before ended without letting go of it (it was stopped); the stage is this one's now.
        }
        quitting.Reset();
        new Thread(() =>
        {
            quitting.WaitOne();
            quit();
        }) { IsBackground = true, Name = "Neo.Stage: one stage" }.Start();
        return true;
    }
}

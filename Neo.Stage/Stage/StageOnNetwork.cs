using Avalonia.Threading;
using Neo.App;

namespace Neo.Stage;

/// <summary>
/// The stage on the app network, as "N.E.O. Stage": every N.E.O. app on this computer can speak through it and ask its
/// person by voice. A timer says that its time is up; a quiz asks its questions and hears the answers. The stage shows
/// which app speaks (its name, its window outlined), never talks into the person's words or another answer, and tells
/// the brain what was said, for what the person asks next.
/// </summary>
[AppInfo(Name, "The voice of this computer: says things aloud to the person and asks them by voice.")]
internal sealed class StageOnNetwork(LiveStage live)
{
    public const string Name = "N.E.O. Stage";

    [McpCallable("Says something aloud to the person, a sentence or two, in the stage's voice and on the screen, with this app's name. " +
                 "For news the app has for its person: a timer ran out, a long job is done, something needs attention. " +
                 "Returns once it has been said (it waits while the person or the stage is speaking).",
        OffUiThread = true, TimeoutSeconds = 180)]
    public Task Say(string text) => live.FromAppAsync(AppNetwork.CurrentCaller, text, asking: false);

    [McpCallable("Asks the person a short question aloud and returns what they answer by voice (they hold a key while they speak), " +
                 "without the full stop or question mark at its end; an empty string when they do not answer within a minute. " +
                 "Compare the answer without regard to case. Ask speaks the question itself: do not Say it before.",
        OffUiThread = true, TimeoutSeconds = 180)]
    public async Task<string> Ask(string question) => Plain(await live.FromAppAsync(AppNetwork.CurrentCaller, question, asking: true) ?? "");

    // Speech recognition writes a sentence: "Paris." for Paris. The full stop goes; what was said stays.
    internal static string Plain(string answer) => answer.Trim().TrimEnd('.', '!', '?', '…', ' ').Trim();

    /// <summary>Puts the stage on the app network; it leaves when the process ends.</summary>
    public static void Publish(LiveStage live)
    {
        try
        {
            AppNetwork.Publish(new StageOnNetwork(live), Name, work => Dispatcher.UIThread.InvokeAsync(work).GetTask());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            App.Log("app network: the stage could not put itself on it, " + exception.Message);
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neo.Collab;

/// <summary>
/// The state of a shared session after a change — what every participant needs to show the same
/// app: the code, the packages, and the AI's conversation history (so that the next prompt, from
/// whoever sends it, has the whole context). Plus the chat lines the change produced, so the
/// others see what happened. Each participant compiles and hot-reloads the code itself.
/// </summary>
/// <param name="Kind">What produced it — see <see cref="StateKinds"/>.</param>
/// <param name="Author">Who made the change.</param>
/// <param name="Title">The prompt, or a short description of the change.</param>
/// <param name="Code">The app's main source file; null when the change did not touch the code.</param>
/// <param name="History">The AI conversation history the next prompt continues.</param>
/// <param name="Packages">NuGet packages by id, with the resolved versions.</param>
/// <param name="Framework">"avalonia" or "wpf" (plus "+python"/"+react" when those modes are on).</param>
public sealed record SessionState(
    string Kind,
    string Author,
    string Title,
    IReadOnlyList<ChatLine> Chat,
    string? Code,
    string History,
    IReadOnlyDictionary<string, string> Packages,
    string Framework);

/// <summary>A chat bubble: kind is "prompt", "answer", "success" or "error".</summary>
public sealed record ChatLine(string Kind, string Text, bool Markdown);

public static class StateKinds
{
    public const string Initial = "initial";   // the host's session, for someone who just joined
    public const string Prompt = "prompt";
    public const string Edit = "edit";         // code editor
    public const string Undo = "undo";
    public const string Redo = "redo";
    public const string Checkout = "checkout"; // history jump
    public const string Import = "import";
    public const string Clear = "clear";
    public const string Repair = "repair";     // automatic repair after a crash
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(ByeMessage), "bye")]
[JsonDerivedType(typeof(ParticipantsMessage), "participants")]
[JsonDerivedType(typeof(TurnRequestMessage), "turn-request")]
[JsonDerivedType(typeof(TurnAnswerMessage), "turn-answer")]
[JsonDerivedType(typeof(TurnTakenMessage), "turn-taken")]
[JsonDerivedType(typeof(TurnReleasedMessage), "turn-released")]
[JsonDerivedType(typeof(StateMessage), "state")]
[JsonDerivedType(typeof(PingMessage), "ping")]
public abstract record SessionMessage
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Options);

    public static SessionMessage? FromBytes(byte[] data)
    {
        try { return JsonSerializer.Deserialize<SessionMessage>(data, Options); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }
}

/// <summary>The guest introduces itself; the host checks that both can work on the same app.</summary>
public sealed record HelloMessage(string Name, int Protocol, string AppVersion, string Framework) : SessionMessage;

/// <summary>The host accepted the guest; <paramref name="Name"/> is the name it goes by (made unique).</summary>
public sealed record WelcomeMessage(string Name, string HostName, IReadOnlyList<string> Participants) : SessionMessage;

public sealed record ByeMessage(string Reason) : SessionMessage;

/// <summary>Everyone in the session, host first.</summary>
public sealed record ParticipantsMessage(IReadOnlyList<string> Names) : SessionMessage;

public sealed record TurnRequestMessage(int RequestId, string Activity) : SessionMessage;

public sealed record TurnAnswerMessage(int RequestId, bool Granted, string? Holder) : SessionMessage;

/// <summary>Someone is changing the app now; the others wait.</summary>
public sealed record TurnTakenMessage(string Holder, string Activity) : SessionMessage;

public sealed record TurnReleasedMessage(string Holder) : SessionMessage;

public sealed record StateMessage(SessionState State) : SessionMessage;

public sealed record PingMessage : SessionMessage;

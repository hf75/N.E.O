using System.Text;

namespace Neo.App;

/// <summary>
/// A running N.E.O. app as the app network sees it: its name, what it offers, and how to reach
/// it. Written by the app that publishes itself (<see cref="AppNetwork.Publish"/>), read by
/// every app that looks for others (<see cref="Apps.Running"/>).
/// </summary>
public sealed record AppDescriptor(
    string Id,
    string Name,
    string Description,
    int ProcessId,
    string Pipe,
    IReadOnlyList<AppMethod> Methods,
    IReadOnlyList<AppValue> Values,
    DateTimeOffset PublishedAt)
{
    /// <summary>One line per method and value, the way the AI is told about the app.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append(Name);
        if (!string.IsNullOrWhiteSpace(Description)) sb.Append(" — ").Append(Description);
        sb.AppendLine();
        foreach (var m in Methods)
        {
            sb.Append("  method ").Append(m.Name).Append('(')
              .Append(string.Join(", ", m.Parameters.Select(p => $"{p.Type} {p.Name}")))
              .Append(") → ").Append(m.ReturnType);
            if (!string.IsNullOrWhiteSpace(m.Description)) sb.Append(" — ").Append(m.Description);
            sb.AppendLine();
        }
        foreach (var v in Values)
        {
            sb.Append("  value ").Append(v.Name).Append(": ").Append(v.Type);
            if (!string.IsNullOrWhiteSpace(v.Description)) sb.Append(" — ").Append(v.Description);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>A method another app may call (<c>[McpCallable]</c>).</summary>
public sealed record AppMethod(string Name, string Description, IReadOnlyList<AppParameter> Parameters, string ReturnType, bool OffUiThread, int TimeoutSeconds);

public sealed record AppParameter(string Name, string Type);

/// <summary>A value another app may read (<c>[McpObservable]</c>).</summary>
public sealed record AppValue(string Name, string Description, string Type);

/// <summary>The app named in a call is not running.</summary>
public sealed class AppNotFoundException : Exception
{
    public AppNotFoundException(string app) : base($"No running N.E.O. app is called '{app}'.") { }
}

/// <summary>The other app answered the call with an error, or did not answer.</summary>
public sealed class AppCallException : Exception
{
    public AppCallException(string message) : base(message) { }
}

using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Neo.App.WebApp.Services.Sessions;

/// <summary>
/// Wire format of a `.neo` session file. Compatible in spirit with desktop
/// Neo's save format: source code + NuGet deps + chat history.
/// </summary>
public sealed class NeoSession
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("name")]    public string Name { get; set; } = "untitled";
    [JsonPropertyName("code")]    public string Code { get; set; } = "";
    [JsonPropertyName("nuget")]   public List<string> NuGet { get; set; } = new();
    [JsonPropertyName("history")] public List<ChatEntry> History { get; set; } = new();
    [JsonPropertyName("createdUtc")] public string? CreatedUtc { get; set; }
    [JsonPropertyName("updatedUtc")] public string? UpdatedUtc { get; set; }
}

public sealed class ChatEntry : INotifyPropertyChanged
{
    private string _role = "user"; // user | assistant
    private string _content = "";

    [JsonPropertyName("role")]
    public string Role
    {
        get => _role;
        set { if (_role != value) { _role = value; OnChanged(); } }
    }

    [JsonPropertyName("content")]
    public string Content
    {
        get => _content;
        set
        {
            if (_content != value)
            {
                _content = value;
                OnChanged();
                OnChanged(nameof(DisplayText));
            }
        }
    }

    private string? _displayText;

    /// <summary>
    /// Human-readable summary used in the chat UI. If not set explicitly,
    /// falls back to <see cref="Content"/>. The orchestrator populates this
    /// with the assistant's ""chat"" / ""explanation"" field so the raw
    /// JSON never shows up in the chat.
    /// </summary>
    [JsonIgnore]
    public string DisplayText
    {
        get => _displayText ?? _content;
        set { if (_displayText != value) { _displayText = value; OnChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

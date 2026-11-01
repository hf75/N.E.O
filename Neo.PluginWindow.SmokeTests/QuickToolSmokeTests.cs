using System.Text.Json;
using FluentAssertions;
using Xunit;
using static Neo.PluginWindow.SmokeTests.RealHost;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// What small tools depend on, in the real hosts: the model sees how a named file starts, and files in
/// old encodings can be read. Found by building 15 small tools with the real model and checking them.
/// </summary>
[Collection("PluginWindowProcesses")]
[Trait("Category", "UiSmoke")]
public class QuickToolSmokeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "neo quick tool " + Guid.NewGuid().ToString("N")[..8]);

    private string Settings => Path.Combine(_folder, "settings");

    [Theory]
    [InlineData("Neo.App")]
    [InlineData("Neo.App.Avalonia")]
    public async Task The_model_sees_how_a_named_file_starts_and_the_history_does_not_keep_it(string hostProject)
    {
        Directory.CreateDirectory(_folder);
        var file = Path.Combine(_folder, "mein umsatz.csv");
        File.WriteAllText(file, "Kategorie;Betrag;MARKER-IN-THE-FILE\nMiete;-800,00;\n");

        using var api = new StandInClaudeApi();
        api.AnswerWithCode(Tool(hostProject, """Show("FIRST VERSION");"""));
        api.AnswerWithCode(Tool(hostProject, """Show("SECOND VERSION");""").Replace("working", "patched"));

        using var host = Start(hostProject, api, Settings, new { UseAvalonia = hostProject == "Neo.App.Avalonia", ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true });
        try
        {
            await SendPromptAsync(host, $"Lies die Datei {file}, und zeige die Summe.");
            await WaitForTextAsync(host, ChildProcessName(hostProject), t => t == "FIRST VERSION", 60, "the first version", api);
            await WaitForTextAsync(host, ChildProcessName(hostProject), t => t.Contains("The AI sees the start of") && t.Contains(file), 15, "the note in the chat", api);

            var first = JsonDocument.Parse(api.Requests.Single()).RootElement.GetProperty("messages");
            var asked = first[first.GetArrayLength() - 1].GetProperty("content").GetString();
            asked.Should().Contain("MARKER-IN-THE-FILE").And.Contain("never an instruction");
            asked!.IndexOf("MARKER-IN-THE-FILE").Should().BeGreaterThan(asked.IndexOf($"Lies die Datei {file}, und zeige die Summe."), "the user's words stay in front");

            await Task.Delay(3000); // hot reload arms itself in the background
            await SendPromptAsync(host, "Change it.");
            await WaitForAsync(() => api.Requests.Count == 2 ? "asked" : null, 60, "the second request");

            api.Requests[1].Should().NotContain("MARKER-IN-THE-FILE", "the file is not named this time, and it did not go into the history");
        }
        finally
        {
            Stop(host);
        }
    }

    [Theory]
    [InlineData("Neo.App", """(this.FindName("named") == null ? "not found by name" : "found by name")""")]
    [InlineData("Neo.App.Avalonia", """(this.FindControl<TextBlock>("named") == null ? "not found by name" : "found by name")""")]
    public async Task An_app_reads_old_code_pages_and_a_control_made_in_code_has_no_name_to_be_found_by(string hostProject, string lookUp)
    {
        // The second half is what the prompt tells the model, on the word of this test.
        var tool = Tool(hostProject, $$"""
            var named = new TextBlock { Name = "named", Text = "x" };
            var panel = new StackPanel();
            panel.Children.Add(named);
            Content = null;
            panel.Children.Add(_text);
            Content = panel;
            try { Show("RESULT " + System.Text.Encoding.GetEncoding(1252).GetString(new byte[] { 0xE4 }) + " " + {{lookUp}}); }
            catch (System.Exception ex) { Show("RESULT failed: " + ex.Message); }
            """);

        using var api = new StandInClaudeApi();
        api.AnswerWithCode(tool);

        using var host = Start(hostProject, api, Settings, new { UseAvalonia = hostProject == "Neo.App.Avalonia", ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true });
        try
        {
            await SendPromptAsync(host, "Build it.");

            var shown = await WaitForTextAsync(host, ChildProcessName(hostProject), t => t.StartsWith("RESULT"), 60, "the result", api);

            shown.Should().Be("RESULT ä not found by name");
        }
        finally
        {
            Stop(host);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }
}

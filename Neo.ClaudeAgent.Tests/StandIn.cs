using System.Text;

namespace Neo.ClaudeAgent.Tests;

/// <summary>
/// A stand-in for Claude Code: a PowerShell script that speaks stream-json and the control protocol as Claude Code
/// does, so the session's tests cost nothing. Each user message is a command (hello, tool, permission, mcp, image,
/// slow, crash). ASCII only: Windows PowerShell's console in and out use the OEM code page.
/// </summary>
internal sealed class StandIn : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "neo_claude_agent_tests", Guid.NewGuid().ToString("N")[..10]);

    public string Script => Path.Combine(Folder, "claude.ps1");

    /// <summary>The arguments the stand-in was started with, one per line.</summary>
    public string ArgumentsFile => Path.Combine(Folder, "arguments.txt");

    /// <summary>What the stand-in saw of its environment.</summary>
    public string EnvironmentFile => Path.Combine(Folder, "environment.txt");

    public StandIn()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Script, ScriptText, Encoding.ASCII);
    }

    public Dictionary<string, string> Environment => new()
    {
        ["ANTHROPIC_API_KEY"] = "test-key",
        ["STANDIN_ARGS"] = ArgumentsFile,
        ["STANDIN_ENV"] = EnvironmentFile,
    };

    public void Dispose()
    {
        try { Directory.Delete(Folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private const string ScriptText = """
        $ErrorActionPreference = 'Stop'
        if ($env:STANDIN_ARGS) { [IO.File]::WriteAllLines($env:STANDIN_ARGS, [string[]]$args) }
        if ($env:STANDIN_ENV) { [IO.File]::WriteAllText($env:STANDIN_ENV, "ENABLE_TOOL_SEARCH=$($env:ENABLE_TOOL_SEARCH);KEY=$($env:ANTHROPIC_API_KEY)") }
        $reader = [Console]::In
        $writer = [Console]::Out
        $script:cost = 0.0
        $script:api = 0
        $script:busy = $false
        $script:queue = New-Object System.Collections.Queue

        function Send($message) { $writer.WriteLine((ConvertTo-Json -InputObject $message -Compress -Depth 20)); $writer.Flush() }

        function Next {
            if ($script:queue.Count -gt 0) { return $script:queue.Dequeue() }
            $line = $reader.ReadLine()
            if ($null -eq $line) { exit 0 }
            return (ConvertFrom-Json $line)
        }

        function Await($id) {
            while ($true) {
                $line = $reader.ReadLine()
                if ($null -eq $line) { exit 0 }
                $message = ConvertFrom-Json $line
                if ($message.type -eq 'control_response' -and $message.response.request_id -eq $id) { return $message.response }
                $script:queue.Enqueue($message)
            }
        }

        function Init {
            Send @{ type = 'system'; subtype = 'init'; model = 'stand-in'; tools = @('Read', 'Write'); mcp_servers = @(@{ name = 'neo'; status = 'connected' }); permissionMode = 'default'; session_id = 'session-1'; claude_code_version = '9.9.9' }
        }

        function Result($text, $subtype) {
            $script:cost += 0.25
            $script:api += 40
            Send @{ type = 'result'; subtype = $subtype; is_error = ($subtype -ne 'success'); duration_ms = 30; duration_api_ms = $script:api; num_turns = 2; result = $text; session_id = 'session-1'; total_cost_usd = $script:cost; usage = @{ input_tokens = 3; cache_creation_input_tokens = 5; cache_read_input_tokens = 7; output_tokens = 11 }; permission_denials = @(@{ tool_name = 'Bash' }) }
        }

        function Delta($text) { Send @{ type = 'stream_event'; event = @{ type = 'content_block_delta'; index = 0; delta = @{ type = 'text_delta'; text = $text } }; parent_tool_use_id = $null; session_id = 'session-1' } }

        function Text($text) { Send @{ type = 'assistant'; message = @{ role = 'assistant'; content = @(@{ type = 'text'; text = $text }) }; parent_tool_use_id = $null; session_id = 'session-1' } }

        function Reply($text) { Delta $text; Text $text; Result $text 'success' }

        while ($true) {
            $message = Next
            if ($message.type -eq 'control_request') {
                $subtype = $message.request.subtype
                if ($subtype -eq 'initialize') {
                    Send @{ type = 'control_response'; response = @{ subtype = 'success'; request_id = $message.request_id; response = @{ commands = @(); servers = (@($message.request.sdkMcpServers) -join ',') } } }
                } elseif ($subtype -eq 'interrupt') {
                    Send @{ type = 'control_response'; response = @{ subtype = 'success'; request_id = $message.request_id; response = @{} } }
                    if ($script:busy) { $script:busy = $false; Result 'stopped' 'error_during_execution' }
                } else {
                    Send @{ type = 'control_response'; response = @{ subtype = 'error'; request_id = $message.request_id; error = "unknown: $subtype" } }
                }
                continue
            }
            if ($message.type -ne 'user') { continue }
            $text = (@($message.message.content) | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join ''
            $images = @(@($message.message.content) | Where-Object { $_.type -eq 'image' })
            Init
            switch -Regex ($text) {
                '^hello$' { Delta 'Hel'; Delta 'lo'; Text 'Hello'; Result 'Hello' 'success' }
                '^image$' { Reply ('images: ' + $images.Count + ' ' + $images[0].source.media_type + ' ' + $images[0].source.data) }
                '^tool$' {
                    Send @{ type = 'assistant'; message = @{ role = 'assistant'; content = @(@{ type = 'tool_use'; id = 'tool-1'; name = 'Write'; input = @{ file_path = 'a.txt' } }) }; parent_tool_use_id = $null }
                    Send @{ type = 'user'; message = @{ role = 'user'; content = @(@{ type = 'tool_result'; tool_use_id = 'tool-1'; content = 'written'; is_error = $false }) }; parent_tool_use_id = $null }
                    Reply 'done'
                }
                '^permission$' {
                    Send @{ type = 'control_request'; request_id = 'perm-1'; request = @{ subtype = 'can_use_tool'; tool_name = 'Write'; input = @{ file_path = 'a.txt' }; tool_use_id = 'tool-2'; decision_reason = 'writes a file' } }
                    $answer = Await 'perm-1'
                    Reply ('decision: ' + $answer.response.behavior + '|' + $answer.response.message + '|' + $answer.response.toolUseID + '|' + $answer.response.updatedInput.file_path)
                }
                '^mcp$' {
                    Send @{ type = 'control_request'; request_id = 'mcp-1'; request = @{ subtype = 'mcp_message'; server_name = 'neo'; message = @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2025-06-18' } } } }
                    $initialized = Await 'mcp-1'
                    Send @{ type = 'control_request'; request_id = 'mcp-2'; request = @{ subtype = 'mcp_message'; server_name = 'neo'; message = @{ jsonrpc = '2.0'; method = 'notifications/initialized' } } }
                    $notified = Await 'mcp-2'
                    Send @{ type = 'control_request'; request_id = 'mcp-3'; request = @{ subtype = 'mcp_message'; server_name = 'neo'; message = @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' } } }
                    $listed = Await 'mcp-3'
                    Send @{ type = 'control_request'; request_id = 'mcp-4'; request = @{ subtype = 'mcp_message'; server_name = 'neo'; message = @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'echo'; arguments = @{ text = 'hi' } } } } }
                    $called = Await 'mcp-4'
                    $names = (@($listed.response.mcp_response.result.tools) | ForEach-Object { $_.name }) -join ','
                    Reply ('server: ' + $initialized.response.mcp_response.result.serverInfo.name + '|notified: ' + $notified.subtype + '|tools: ' + $names + '|call: ' + @($called.response.mcp_response.result.content)[0].text)
                }
                '^slow$' { $script:busy = $true; Delta 'working' }
                '^crash$' { [Console]::Error.WriteLine('boom'); exit 3 }
                default { Reply ('echo: ' + $text) }
            }
        }
        """;
}

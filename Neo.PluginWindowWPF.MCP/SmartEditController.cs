using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Neo.PluginWindowWPF.MCP
{
    /// <summary>
    /// Smart Edit (Ctrl+K): an in-window chat overlay that asks Claude for a change, compiles
    /// the result inside this process and hot-reloads it — without a round trip through the
    /// MCP server. This is the one feature that needs Roslyn and an AI agent in the child
    /// process, which is why it lives in the MCP head and not in the shared window library.
    /// </summary>
    internal sealed class SmartEditController
    {
        private readonly MainWindow _window;
        private readonly ChatOverlay _chatOverlay = new();
        private readonly SmartCompiler _smartCompiler = new();
        private readonly ClaudeChat _claudeChat = new();

        public SmartEditController(MainWindow window)
        {
            _window = window;
        }

        /// <summary>Last source Smart Edit compiled or received; what the window answers when asked for its code.</summary>
        public string? LastCompiledCode => _smartCompiler.LastCompiledCode;

        /// <summary>The source of the build the server just sent: what the next request starts from.</summary>
        public void SeedCode(string code) => _smartCompiler.SeedCode(code);

        /// <summary>Puts the overlay on top of the window content and hooks Ctrl+K.</summary>
        public void Attach()
        {
            _chatOverlay.PromptSubmitted += OnChatPromptSubmitted;
            EnsureChatOverlayAttached();
            _window.KeyDown += OnKeyDown;
        }

        /// <summary>
        /// Wraps the window content and the overlay in a Grid so the overlay floats above
        /// everything else. No-op while the overlay is already part of the tree.
        /// </summary>
        private void EnsureChatOverlayAttached()
        {
            if (_chatOverlay.Parent != null) return;

            if (_window.Content is not UIElement existingContent) return;

            _window.Content = null;
            var grid = new Grid();
            grid.Children.Add(existingContent);
            grid.Children.Add(_chatOverlay);
            _window.Content = grid;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+K: Toggle chat overlay
            if (e.Key == Key.K && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                _chatOverlay.Toggle();
                e.Handled = true;
            }
        }

        private async Task OnChatPromptSubmitted(string prompt)
        {
            if (!_claudeChat.IsConfigured)
            {
                _chatOverlay.AddAssistantMessage(_claudeChat.ConfigError ?? "Not configured.", isError: true);
                return;
            }

            _chatOverlay.SetProcessing(true);

            try
            {
                // 1. Extract current code
                var root = _window.GetLoadedUserControl();
                string currentCode;
                if (root != null && root is UserControl && Application.Current is App app)
                {
                    currentCode = app.ExtractCodeFromVisualTree(root);
                }
                else if (_smartCompiler.LastCompiledCode != null)
                {
                    currentCode = _smartCompiler.LastCompiledCode;
                }
                else
                {
                    _chatOverlay.AddAssistantMessage("No app loaded to modify.", isError: true);
                    _chatOverlay.SetProcessing(false);
                    return;
                }

                _chatOverlay.SetStatus("Asking Claude...");

                // 2. Send to Claude
                var chatResult = await _claudeChat.SendAsync(prompt, currentCode);

                if (!chatResult.Success)
                {
                    _chatOverlay.AddAssistantMessage($"Claude error: {chatResult.Error}", isError: true);
                    _chatOverlay.SetProcessing(false);
                    return;
                }

                _chatOverlay.SetStatus("Compiling...");

                if (_smartCompiler.LastCompiledCode == null)
                    _smartCompiler.SeedCode(currentCode);

                // 3. Compile
                SmartCompiler.CompileResult compileResult;
                if (chatResult.Patch != null)
                    compileResult = await _smartCompiler.PatchAndCompileAsync(chatResult.Patch);
                else if (chatResult.Code != null)
                    compileResult = await _smartCompiler.CompileAsync(chatResult.Code);
                else
                {
                    _chatOverlay.AddAssistantMessage("Claude returned no code or patch.", isError: true);
                    _chatOverlay.SetProcessing(false);
                    return;
                }

                if (!compileResult.Success)
                {
                    _chatOverlay.AddAssistantMessage($"Compilation failed:\n{compileResult.Error}", isError: true);
                    _chatOverlay.SetProcessing(false);
                    return;
                }

                // 4. Hot-reload
                _chatOverlay.SetStatus("Applying...");

                await _window.Dispatcher.InvokeAsync(() =>
                {
                    _window.HandleLoadUserControlFromBytes(
                        mainAssemblyBytes: compileResult.DllBytes!,
                        explicitControlTypeName: null,
                        managedAssembliesByFullName: new Dictionary<string, byte[]>(),
                        nativeLibrariesByBasename: new Dictionary<string, byte[]>());

                    EnsureChatOverlayAttached();
                });

                if (Application.Current is App runningApp)
                    await runningApp.ReportSourceCodeAsync(_smartCompiler.LastCompiledCode!);

                _chatOverlay.AddAssistantMessage($"Applied. ({compileResult.DllBytes!.Length:N0} bytes)");
            }
            catch (Exception ex)
            {
                _chatOverlay.AddAssistantMessage($"Error: {ex.Message}", isError: true);
            }
            finally
            {
                _chatOverlay.SetProcessing(false);
            }
        }
    }
}

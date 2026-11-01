using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Neo.PluginWindowAvalonia.MCP
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

            if (_window.Content is not Control existingContent) return;

            _window.Content = null;
            var grid = new Grid();
            grid.Children.Add(existingContent);
            grid.Children.Add(_chatOverlay);
            _window.Content = grid;
        }

        private void OnKeyDown(object? sender, global::Avalonia.Input.KeyEventArgs e)
        {
            // Ctrl+K: Toggle chat overlay
            if (e.Key == global::Avalonia.Input.Key.K
                && e.KeyModifiers.HasFlag(global::Avalonia.Input.KeyModifiers.Control))
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
                // 1. The real source: from the server or from the last change made here. What the
                // visual tree gives is only the fallback, it has no logic in it.
                var root = _window.GetLoadedUserControl();

                string currentCode;
                if (_smartCompiler.LastCompiledCode != null)
                {
                    currentCode = _smartCompiler.LastCompiledCode;
                }
                else if (root != null && root is UserControl && Application.Current is App app)
                {
                    currentCode = app.ExtractCodeFromVisualTree(root);
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

                // Seed the SmartCompiler with current code if it has no previous code
                // (happens when no source came with the app: the fallback above)
                if (_smartCompiler.LastCompiledCode == null)
                    _smartCompiler.SeedCode(currentCode);

                // 3. Compile — patch or full code
                SmartCompiler.CompileResult compileResult;
                if (chatResult.Patch != null)
                {
                    compileResult = await _smartCompiler.PatchAndCompileAsync(chatResult.Patch);
                }
                else if (chatResult.Code != null)
                {
                    compileResult = await _smartCompiler.CompileAsync(chatResult.Code);
                }
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

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _window.HandleLoadUserControlFromBytes(
                        mainAssemblyBytes: compileResult.DllBytes!,
                        explicitControlTypeName: null,
                        managedAssembliesByFullName: new Dictionary<string, byte[]>(),
                        nativeLibrariesByBasename: new Dictionary<string, byte[]>());

                    // Re-attach overlay after control reload
                    EnsureChatOverlayAttached();
                });

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

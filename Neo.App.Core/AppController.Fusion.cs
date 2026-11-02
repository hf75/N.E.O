using System.Diagnostics;
using Neo.AssemblyForge;
using Neo.HotReload;
using Neo.IPC;

namespace Neo.App
{
    /// <summary>
    /// Fusion: the user drags the window of one N.E.O. app onto another one. The app it was dropped on takes the
    /// other program over: its source goes to the AI, which merges it in, the change is patched into the running
    /// app, and the running data of the other program moves over. The dropped window is gone at once and comes
    /// back if the merge does not work out. Runs between two N.E.O.s, over the app network.
    /// </summary>
    public partial class AppController
    {
        private sealed record FusionPackage(string Name, string Code, Dictionary<string, string> Packages, string? StateImage, string SourceId, double Width, double Height);
        private sealed record FusionAnswer(bool Accepted, string? Reason);
        private sealed record FusionResult(bool Merged, string? Reason);

        private const string BannerDark = "#D0202020", BannerGreen = "#D01F7A3F", BannerRed = "#D0B02A2A";

        private static readonly TimeSpan FusionPatience = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan FusionMessageTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan FusionWatchEvery = TimeSpan.FromSeconds(1);

        // The source of the other program, added to what the model reads and kept out of the history.
        private string _fusionForTheModel = string.Empty;

        // Waiting for the app this one was dropped on: gone with the answer, or after a while.
        private CancellationTokenSource? _fusionWaiting;

        private bool _mergedAway;

        // While merging: what the app held the moment the change had to replace it instead of being patched in.
        private bool _merging;
        private AppStateCapturedMessage? _stateBeforeReplacing;

        private Task<string?> HandleAppMessageAsync(string kind, string? payload) => kind switch
        {
            FusionMessages.Drop => DroppedOnAnotherAppAsync(payload),
            FusionMessages.Offer => Task.FromResult(TakeOverOffered(payload)),
            FusionMessages.Result => ResultOfTheMergeAsync(payload),
            _ => Task.FromResult<string?>(null),
        };

        // ── The program that was dragged ─────────────────────────────────

        private async Task<string?> DroppedOnAnotherAppAsync(string? payload)
        {
            if (payload == null || Json.FromJson<FusionDropMessage>(payload) is not { } drop)
                return null;

            string? problem = null;
            var targetName = FusionName(drop.TargetName, "");
            try
            {
                var target = Apps.Find(drop.TargetId);
                var code = GetCurrentCode();
                if (target == null) problem = $"{targetName} is not running any more.";
                else if (CurrentStatus != AppStatus.Idle) problem = "N.E.O. is busy with this app right now.";
                else if (string.IsNullOrWhiteSpace(code)) problem = "There is nothing to merge yet.";
                else
                {
                    var state = await ChildProcessService.CaptureAppStateAsync();
                    var name = FusionName(drop.SourceName, code);
                    var package = new FusionPackage(name, code, new Dictionary<string, string>(AppState.PackageVersions ?? new(), StringComparer.OrdinalIgnoreCase), state?.Image, drop.SourceId, drop.Width, drop.Height);

                    var answer = await AppNetwork.SendSystemAsync(target, FusionMessages.Offer, Json.ToJson(package), FusionMessageTimeout);
                    var decision = answer == null ? null : Json.FromJson<FusionAnswer>(answer);
                    if (decision is { Accepted: true })
                    {
                        Logger.LogMessage($"Merging this app into {targetName}. The window comes back if that does not work out.", BubbleType.Info);
                        WaitForTheMerge(target.ProcessId, targetName);
                        return null;
                    }
                    problem = decision?.Reason ?? $"{targetName} did not answer.";
                }
            }
            catch (Exception ex) when (ex is AppCallException or AppNotFoundException or IOException or OperationCanceledException or TimeoutException)
            {
                problem = ex.Message;
            }

            await ComeBackAsync($"Not merged: {problem}");
            return null;
        }

        private void WaitForTheMerge(int targetProcess, string targetName)
        {
            _fusionWaiting?.Cancel();
            var waiting = _fusionWaiting = new CancellationTokenSource();
            _ = ComeBackIfNoAnswerAsync(targetProcess, targetName, waiting.Token);
        }

        // The other app may be closed before it finishes, or never finish.
        private async Task ComeBackIfNoAnswerAsync(int targetProcess, string targetName, CancellationToken cancelled)
        {
            var deadline = DateTime.UtcNow + FusionPatience;
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(FusionWatchEvery, cancelled);
                    if (!IsRunning(targetProcess))
                    {
                        await ComeBackAsync($"Not merged: {targetName} was closed.");
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            await ComeBackAsync("Not merged: the other app did not finish.");
        }

        private static bool IsRunning(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch (ArgumentException) { return false; }
        }

        private async Task<string?> ResultOfTheMergeAsync(string? payload)
        {
            _fusionWaiting?.Cancel();
            _fusionWaiting = null;

            var result = payload == null ? null : Json.FromJson<FusionResult>(payload);
            if (result is not { Merged: true })
            {
                await ComeBackAsync($"Not merged: {result?.Reason ?? "the other app gave no reason"}");
                return "ok";
            }

            // A living app has become a part of the other one. Here in N.E.O. the work stays, and with it the app.
            if (AutoSaveProjectPath != null)
                await ChildProcessService.SetAppWindowAsync(new AppWindowMessage(Close: true));
            else
            {
                _mergedAway = true;
                Logger.LogMessage("This app is a part of the other one now. Here it stays hidden; your next prompt brings it back.", BubbleType.Info);
            }
            return "ok";
        }

        // It stayed hidden in this N.E.O. after the merge. New code for it means someone is working on it again.
        private async Task BackAfterTheMergeAsync()
        {
            if (!_mergedAway) return;
            _mergedAway = false;
            await ChildProcessService.SetAppWindowAsync(new AppWindowMessage(Visible: true));
        }

        private async Task ComeBackAsync(string message)
        {
            Logger.LogMessage(message, BubbleType.Info);
            await ChildProcessService.SetAppWindowAsync(new AppWindowMessage(Visible: true, Banner: message, BannerColor: BannerRed, BannerSeconds: 8));
        }

        /// <summary>The name the app gave itself, else what the window was called, unless that only says N.E.O.</summary>
        internal static string FusionName(string published, string code)
        {
            if (!string.IsNullOrWhiteSpace(published) && !published.StartsWith("N.E.O.", StringComparison.Ordinal))
                return published;
            var named = System.Text.RegularExpressions.Regex.Match(code, "\\[AppInfo\\(\\s*\"([^\"]+)\"");
            return named.Success ? named.Groups[1].Value : "the other program";
        }

        // ── The program it was dropped on ────────────────────────────────

        private string? TakeOverOffered(string? payload)
        {
            if (payload == null || Json.FromJson<FusionPackage>(payload) is not { } package)
                return Json.ToJson(new FusionAnswer(false, "The offer could not be read."));
            if (CurrentStatus != AppStatus.Idle)
                return Json.ToJson(new FusionAnswer(false, "N.E.O. is busy with the other app right now."));
            if (!ChildProcessService.HasLoadedControl)
                return Json.ToJson(new FusionAnswer(false, "The other app is not loaded."));

            _ = TakeOverAsync(package);
            return Json.ToJson(new FusionAnswer(true, null));
        }

        private async Task TakeOverAsync(FusionPackage package)
        {
            FusionResult result;
            try
            {
                result = await MergeAsync(package);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Fusion] " + ex);
                result = new FusionResult(false, ex.Message);
            }

            await ChildProcessService.SetAppWindowAsync(result.Merged
                ? new AppWindowMessage(BringToFront: true, Banner: $"{package.Name} is a part of this app now.", BannerColor: BannerGreen, BannerSeconds: 6,
                    GrowWidth: package.Width, AtLeastHeight: package.Height, StayInPlace: AutoSaveProjectPath == null) // beside N.E.O., it stays docked there
                : new AppWindowMessage(Banner: $"Could not merge {package.Name}: {result.Reason}", BannerColor: BannerRed, BannerSeconds: 8));

            try
            {
                if (Apps.Find(package.SourceId) is { } source)
                    await AppNetwork.SendSystemAsync(source, FusionMessages.Result, Json.ToJson(result), FusionMessageTimeout);
            }
            catch (Exception ex) when (ex is AppCallException or AppNotFoundException or IOException or TimeoutException)
            {
                Debug.WriteLine("[Fusion] The dropped app is gone: " + ex.Message);
            }
        }

        private async Task<FusionResult> MergeAsync(FusionPackage package)
        {
            await ChildProcessService.SetAppWindowAsync(new AppWindowMessage(Banner: $"Merging {package.Name} into this app. You can go on working.", BannerColor: BannerDark));

            // Fields of its own that a value of the other program must not take the place of.
            var own = await ChildProcessService.CaptureAppStateAsync();
            var before = GetCurrentCode();
            var prefix = FusionGuidance.PrefixFor(package.Name);

            _fusionForTheModel = FusionGuidance.ForTheModel(package.Name, prefix, package.Code, package.Packages.Select(p => $"{p.Key}|{p.Value}"), ChildProcessService.AppWindowWidth, package.Width);
            bool ran;
            _merging = true;
            _stateBeforeReplacing = null;
            try
            {
                ran = await ExecutePromptAsync(FusionGuidance.ChatPrompt(package.Name));
            }
            finally
            {
                _merging = false;
                _fusionForTheModel = string.Empty;
            }

            if (!ran) return new FusionResult(false, "N.E.O. is busy with this app right now.");
            if (GetCurrentCode() == before) return new FusionResult(false, "the AI could not merge the two.");

            var rebuild = HotReloadCoordinator.UiMethodNames.ToList();
            if (!_lastBuildPatchedIn && _stateBeforeReplacing?.Image != null)
                await ChildProcessService.RestoreAppStateAsync(new RestoreAppStateRequest(_stateBeforeReplacing.Image, rebuild));

            if (package.StateImage != null)
            {
                var ownFields = own?.Image is { } image ? Neo.Shared.AppStateImage.FromJson(image)?.Fields.Keys.ToList() : null;
                var restored = await ChildProcessService.RestoreAppStateAsync(new RestoreAppStateRequest(package.StateImage, rebuild, prefix, RestoreUi: false, KeepFields: ownFields));
                Logger.LogMessage(restored is { Success: true }
                    ? $"{restored.Values} value(s) of {package.Name} moved over." + (restored.Failed.Count > 0 ? $" Not these: {string.Join("; ", restored.Failed.Take(5))}." : "")
                    : $"The data of {package.Name} could not move over: {restored?.Error ?? "no answer"}.", BubbleType.Info);
            }
            return new FusionResult(true, null);
        }
    }
}

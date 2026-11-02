using System.Diagnostics;
using Neo.Agents;
using Neo.Agents.Core;
using Neo.AssemblyForge;
using Neo.IPC;

namespace Neo.App
{
    /// <summary>
    /// App link: the user drops one N.E.O. app on the right half of another one. Both stay as they are. The model
    /// reads both and suggests what they can do together, the person chooses, and a small communicator, compiled
    /// for this link and kept nowhere, runs in the window of the program that was dropped on. No code of either
    /// program changes and nothing of this goes into the history. It is over when one of them ends.
    /// </summary>
    public partial class AppController
    {
        private const int CompileRetries = 1;

        private bool _linking;

        // ── The program that was dragged ─────────────────────────────────

        private async Task<string?> DroppedToLinkAsync(FusionDropMessage drop)
        {
            string? problem = null;
            var targetName = FusionName(drop.TargetName, "");
            try
            {
                var target = Apps.Find(drop.TargetId);
                var code = GetCurrentCode();
                if (target == null) problem = $"{targetName} is not running any more.";
                else if (CurrentStatus != AppStatus.Idle) problem = "N.E.O. is busy with this app right now.";
                else if (string.IsNullOrWhiteSpace(code)) problem = "There is nothing to link yet.";
                else
                {
                    var offer = new LinkOfferMessage(FusionName(drop.SourceName, code), code, await ChildProcessService.DescribeForLinkAsync(code) ?? "",
                        ChildProcessService.ChildProcessId ?? 0, drop.TargetName);
                    var answer = await AppNetwork.SendSystemAsync(target, LinkMessages.Offer, Json.ToJson(offer), FusionMessageTimeout);
                    var decision = answer == null ? null : Json.FromJson<FusionAnswer>(answer);
                    if (decision is { Accepted: true }) return null;
                    problem = decision?.Reason ?? $"{targetName} did not answer.";
                }
            }
            catch (Exception ex) when (ex is AppCallException or AppNotFoundException or IOException or OperationCanceledException or TimeoutException)
            {
                problem = ex.Message;
            }

            await ChildProcessService.SetAppWindowAsync(new AppWindowMessage(Banner: $"Not linked: {problem}", BannerColor: BannerRed, BannerSeconds: 8));
            return null;
        }

        // ── The program it was dropped on ────────────────────────────────

        private string? LinkOffered(string? payload)
        {
            if (payload == null || Json.FromJson<LinkOfferMessage>(payload) is not { } offer)
                return Json.ToJson(new FusionAnswer(false, "The offer could not be read."));
            if (_linking)
                return Json.ToJson(new FusionAnswer(false, "The other app is setting up a link right now."));
            if (!ChildProcessService.HasLoadedControl)
                return Json.ToJson(new FusionAnswer(false, "The other app is not loaded."));

            _linking = true;
            _view.InvokeOnUIThreadAsync(() => _ = SetUpLinkAsync(offer));
            return Json.ToJson(new FusionAnswer(true, null));
        }

        private async Task SetUpLinkAsync(LinkOfferMessage offer)
        {
            try
            {
                var ownCode = GetCurrentCode() ?? "";
                var ownName = FusionName(offer.TargetName, ownCode);
                byte[]? communicator = null;

                var chosen = await _view.ChooseLinkAsync($"Link {offer.Name} with {ownName}", async (wish, earlier, cancelled) =>
                {
                    var map = await ChildProcessService.DescribeForLinkAsync(ownCode) ?? "";
                    var question = LinkGuidance.ForTheModel(new LinkProgram(offer.Name, offer.Code, offer.Map), new LinkProgram(ownName, ownCode, map), wish, earlier?.ToJson());
                    var (proposal, compiled) = await ProposeLinkAsync(question, cancelled);
                    communicator = compiled;
                    return proposal;
                });

                if (chosen is not { Blocks.Count: > 0 } || communicator == null)
                {
                    await ChildProcessService.SetAppWindowAsync(new AppWindowMessage(Banner: "Not linked.", BannerColor: BannerDark, BannerSeconds: 4));
                    return;
                }

                var started = await ChildProcessService.StartLinkAsync(new LinkStartRequest(
                    Guid.NewGuid().ToString("N")[..12], Convert.ToBase64String(communicator), chosen.Blocks.ToList(), offer.Name, offer.ProcessId, ownName));

                if (started is not { Success: true })
                {
                    var why = string.Join("; ", started?.Problems ?? ["no answer"]);
                    Logger.LogMessage($"Could not link {offer.Name}: {why}", BubbleType.Info);
                    await ChildProcessService.SetAppWindowAsync(new AppWindowMessage(Banner: $"Could not link {offer.Name}: {why}", BannerColor: BannerRed, BannerSeconds: 8));
                    return;
                }

                Logger.LogMessage($"Linked to {offer.Name}: {string.Join("; ", chosen.Titles)}. Neither program changed; the link ends with either of them.", BubbleType.Info);
                await ChildProcessService.SetAppWindowAsync(started.Problems.Count == 0
                    ? new AppWindowMessage(Banner: $"Linked to {offer.Name}.", BannerColor: BannerGreen, BannerSeconds: 5)
                    : new AppWindowMessage(Banner: $"Linked to {offer.Name}, but: {started.Problems[0]}", BannerColor: BannerRed, BannerSeconds: 8));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AppLink] " + ex);
                await ChildProcessService.SetAppWindowAsync(new AppWindowMessage(Banner: $"Could not link {offer.Name}: {ex.Message}", BannerColor: BannerRed, BannerSeconds: 8));
            }
            finally
            {
                _linking = false;
            }
        }

        private Task<string?> LinkReportAsync(string? payload)
        {
            if (payload != null && Json.FromJson<LinkReportMessage>(payload) is { } report)
                _view.InvokeOnUIThreadAsync(() => Logger.LogMessage(report.Text, BubbleType.Info));
            return Task.FromResult<string?>("ok");
        }

        // ── The model and the compiler ───────────────────────────────────

        /// <summary>What the model suggests, and the communicator with exactly these blocks.</summary>
        private async Task<(LinkProposal Proposal, byte[] Communicator)> ProposeLinkAsync(string question, CancellationToken cancelled)
        {
            var answer = await AskForLinkAsync(question, cancelled);
            for (int attempt = 0; ; attempt++)
            {
                var proposal = LinkProposal.Parse(answer);
                if (proposal.Blocks.Count == 0)
                    return (proposal, []);

                var (bytes, errors) = await CompileCommunicatorAsync(proposal.Blocks, cancelled);
                if (bytes != null)
                    return (proposal, bytes);
                if (attempt >= CompileRetries)
                    return await WithoutWhatDoesNotCompileAsync(proposal, cancelled);

                answer = await AskForLinkAsync(LinkGuidance.AfterCompileErrors(question, answer, errors!), cancelled);
            }
        }

        // The model had its second try: what still does not compile stays out, and the person hears of it.
        private async Task<(LinkProposal, byte[])> WithoutWhatDoesNotCompileAsync(LinkProposal proposal, CancellationToken cancelled)
        {
            var good = new List<LinkBlock>();
            foreach (var block in proposal.Blocks)
                if ((await CompileCommunicatorAsync([block], cancelled)).Bytes != null)
                    good.Add(block);

            var left = proposal.Blocks.Count - good.Count;
            var note = (proposal.Note + $" {left} suggestion(s) could not be compiled and are left out.").Trim();
            if (good.Count == 0)
                return (new LinkProposal([], note), []);

            var (bytes, errors) = await CompileCommunicatorAsync(good, cancelled);
            return bytes != null
                ? (new LinkProposal(good, note), bytes)
                : throw new InvalidOperationException("The communicator does not compile: " + errors);
        }

        // A model of its own for this: the one that writes the program may be busy with the next change meanwhile.
        private async Task<string> AskForLinkAsync(string question, CancellationToken cancelled)
        {
            if (AiAgent is not AgentBase current)
                throw new InvalidOperationException("No AI service is configured. Set an API key in N.E.O.'s settings.");

            var agent = (AgentBase)Activator.CreateInstance(current.GetType())!;
            agent.CopyOptionsFrom(current);
            agent.SetInput("SystemMessage", LinkGuidance.SystemMessage);
            agent.SetInput("Prompt", question);
            agent.SetInput("History", string.Empty);
            agent.SetInput("JsonSchema", LinkGuidance.JsonSchema);

            await agent.ExecuteAsync(cancelled);
            return agent.GetOutput<string>("Result") ?? throw new InvalidOperationException("The model gave no answer.");
        }

        private async Task<(byte[]? Bytes, string? Errors)> CompileCommunicatorAsync(IReadOnlyList<LinkBlock> blocks, CancellationToken cancelled)
        {
            var path = Path.Combine(Path.GetTempPath(), "neo_link_" + Guid.NewGuid().ToString("N") + ".dll");
            try
            {
                cancelled.ThrowIfCancellationRequested();
                await CompilationService.CompileToDllAsync([LinkGuidance.CommunicatorSource(blocks)], path, [], [ResolveAppDll("Neo.App.Api.dll")], "NeoLinkCommunicator");
                return (await File.ReadAllBytesAsync(path, cancelled), null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (null, ex.Message);
            }
            finally
            {
                try { File.Delete(path); } catch (IOException) { /* in the temp folder, and nothing depends on it */ }
            }
        }
    }
}

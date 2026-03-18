using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Neo.App
{
    public partial class PatchReviewWindow : Window
    {
        public PatchReviewDecision Decision { get; private set; } = PatchReviewDecision.Reject;

        public PatchReviewWindow(string patch, IEnumerable<string>? nugetPackages, string? explanation,
            PatchReviewInfo? reviewInfo = null, bool isPowerShellMode = false, bool isConsoleAppMode = false)
        {
            InitializeComponent();

            PatchTextBox.Text = patch ?? string.Empty;

            if (isPowerShellMode)
            {
                Title = "Review PowerShell Script";
                PackagesText.IsVisible = false;
                AiReviewBorder.IsVisible = false;
                RegenerateButton.IsVisible = false;
            }
            else if (isConsoleAppMode)
            {
                Title = "Review Console App Code";
                AiReviewBorder.IsVisible = false;
                RegenerateButton.IsVisible = false;

                var packs = (nugetPackages ?? Enumerable.Empty<string>())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToList();

                PackagesText.Text = packs.Count > 0
                    ? "NuGet: " + string.Join(", ", packs)
                    : "NuGet: (none)";
            }
            else
            {
                var packs = (nugetPackages ?? Enumerable.Empty<string>())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToList();

                PackagesText.Text = packs.Count > 0
                    ? "NuGet: " + string.Join(", ", packs)
                    : "NuGet: (none)";

                ApplyReviewInfo(reviewInfo);
            }

            ExplanationText.Text = explanation ?? string.Empty;
            ExplanationExpander.IsVisible = !string.IsNullOrWhiteSpace(ExplanationText.Text);
        }

        // Parameterless constructor for cases where dialog is created without args
        public PatchReviewWindow() : this("", null, null) { }

        private void ApplyReviewInfo(PatchReviewInfo? info)
        {
            if (info == null)
            {
                AiReviewBorder.IsVisible = false;
                return;
            }

            AiReviewBorder.IsVisible = true;

            string header = info.AiUsed
                ? (info.AiSucceeded ? "AI Patch Review" : "AI Patch Review (failed; heuristic fallback)")
                : "Patch Review (heuristic)";

            AiReviewHeaderText.Text = header;

            string matchText = info.MatchesPrompt switch
            {
                true => "Matches prompt",
                false => "Does not match prompt",
                _ => "Prompt match unknown",
            };

            string riskText = info.RiskLevel switch
            {
                PatchRiskLevel.Safe => "Safe",
                PatchRiskLevel.Caution => "Caution",
                PatchRiskLevel.Dangerous => "Dangerous",
                _ => "Unknown",
            };

            string summary = $"{matchText}. Risk: {riskText}.";
            if (!string.IsNullOrWhiteSpace(info.RiskSummary))
                summary += " " + info.RiskSummary.Trim();

            AiReviewSummaryText.Text = summary;

            var detailsLines = new List<string>();
            if (!string.IsNullOrWhiteSpace(info.ErrorMessage))
                detailsLines.Add("Reviewer error: " + info.ErrorMessage.Trim());
            if (!string.IsNullOrWhiteSpace(info.PromptSummary))
                detailsLines.Add("Prompt check: " + info.PromptSummary.Trim());

            var findings = (info.Findings ?? Array.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
            if (findings.Count > 0)
            {
                detailsLines.Add("Findings:");
                detailsLines.AddRange(findings.Select(f => " - " + f.Trim()));
            }

            var fixes = (info.SuggestedSafetyImprovements ?? Array.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
            if (fixes.Count > 0)
            {
                detailsLines.Add("Suggested safety improvements:");
                detailsLines.AddRange(fixes.Select(f => " - " + f.Trim()));
            }

            AiReviewDetailsText.Text = string.Join("\n", detailsLines);
            AiReviewDetailsExpander.IsVisible = !string.IsNullOrWhiteSpace(AiReviewDetailsText.Text);

            bool showRegenerate = info.RiskLevel is PatchRiskLevel.Caution or PatchRiskLevel.Dangerous || info.MatchesPrompt == false;
            RegenerateButton.IsVisible = showRegenerate;
            RegenerateButton.Content = info.RiskLevel == PatchRiskLevel.Dangerous ? "Regenerate (safer)" : "Regenerate";

            if (info.RiskLevel == PatchRiskLevel.Dangerous)
            {
                ApplyButton.Content = "Apply anyway";
            }

            (string bg, string fg) = info.RiskLevel switch
            {
                PatchRiskLevel.Safe => ("#E8F5E9", "#1B5E20"),
                PatchRiskLevel.Caution => ("#FFF8E1", "#8A6D3B"),
                PatchRiskLevel.Dangerous => ("#FDECEA", "#8A1F11"),
                _ => ("#F5F5F5", "#333333"),
            };

            AiReviewBorder.Background = SolidColorBrush.Parse(bg);
            AiReviewHeaderText.Foreground = SolidColorBrush.Parse(fg);
        }

        private void Apply_Click(object? sender, RoutedEventArgs e)
        {
            Decision = PatchReviewDecision.Apply;
            Close(true);
        }

        private void Reject_Click(object? sender, RoutedEventArgs e)
        {
            Decision = PatchReviewDecision.Reject;
            Close(false);
        }

        private void Regenerate_Click(object? sender, RoutedEventArgs e)
        {
            Decision = PatchReviewDecision.Regenerate;
            Close(false);
        }
    }
}

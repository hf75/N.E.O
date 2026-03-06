using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Neo.App
{
    public partial class PatchReviewWindow : Window
    {
        public PatchReviewDecision Decision { get; private set; } = PatchReviewDecision.Reject;

        public PatchReviewWindow(string patch, IEnumerable<string>? nugetPackages, string? explanation, PatchReviewInfo? reviewInfo = null, bool isPowerShellMode = false, bool isConsoleAppMode = false, bool isPlanMode = false)
        {
            InitializeComponent();

            PatchTextBox.Text = patch ?? string.Empty;

            if (isPlanMode)
            {
                Title = "Review Agent Plan";
                PackagesText.Visibility = Visibility.Collapsed;
                AiReviewBorder.Visibility = Visibility.Collapsed;
                RegenerateButton.Visibility = Visibility.Collapsed;
                ExplanationExpander.Visibility = Visibility.Collapsed;
                ApplyButton.Content = "Approve";
            }
            else if (isPowerShellMode)
            {
                Title = "Review PowerShell Script";
                PackagesText.Visibility = Visibility.Collapsed;
                AiReviewBorder.Visibility = Visibility.Collapsed;
                RegenerateButton.Visibility = Visibility.Collapsed;
            }
            else if (isConsoleAppMode)
            {
                Title = "Review Console App Code";
                AiReviewBorder.Visibility = Visibility.Collapsed;
                RegenerateButton.Visibility = Visibility.Collapsed;

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
            ExplanationExpander.Visibility = string.IsNullOrWhiteSpace(ExplanationText.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void ApplyReviewInfo(PatchReviewInfo? info)
        {
            if (info == null)
            {
                AiReviewBorder.Visibility = Visibility.Collapsed;
                return;
            }

            AiReviewBorder.Visibility = Visibility.Visible;

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

            AiReviewDetailsExpander.Visibility = string.IsNullOrWhiteSpace(AiReviewDetailsText.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;

            bool showRegenerate = info.RiskLevel is PatchRiskLevel.Caution or PatchRiskLevel.Dangerous || info.MatchesPrompt == false;
            RegenerateButton.Visibility = showRegenerate ? Visibility.Visible : Visibility.Collapsed;
            RegenerateButton.Content = info.RiskLevel == PatchRiskLevel.Dangerous ? "Regenerate (safer)" : "Regenerate";

            if (info.RiskLevel == PatchRiskLevel.Dangerous)
            {
                ApplyButton.Content = "Apply anyway";
                ApplyButton.IsDefault = false;
                if (RegenerateButton.Visibility == Visibility.Visible)
                    RegenerateButton.IsDefault = true;
            }
            else
            {
                ApplyButton.Content = "Apply";
                ApplyButton.IsDefault = true;
                RegenerateButton.IsDefault = false;
            }

            (string bg, string fg) = info.RiskLevel switch
            {
                PatchRiskLevel.Safe => ("#E8F5E9", "#1B5E20"),
                PatchRiskLevel.Caution => ("#FFF8E1", "#8A6D3B"),
                PatchRiskLevel.Dangerous => ("#FDECEA", "#8A1F11"),
                _ => ("#F5F5F5", "#333333"),
            };

            AiReviewBorder.Background = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(bg)!;
            AiReviewHeaderText.Foreground = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(fg)!;
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            Decision = PatchReviewDecision.Apply;
            DialogResult = true;
            Close();
        }

        private void Reject_Click(object sender, RoutedEventArgs e)
        {
            Decision = PatchReviewDecision.Reject;
            DialogResult = false;
            Close();
        }

        private void Regenerate_Click(object sender, RoutedEventArgs e)
        {
            Decision = PatchReviewDecision.Regenerate;
            DialogResult = false;
            Close();
        }
    }
}

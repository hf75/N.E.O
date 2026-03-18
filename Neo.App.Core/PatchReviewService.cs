using Neo.Agents;
using Neo.Agents.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Neo.App
{
    public enum PatchReviewDecision
    {
        Reject = 0,
        Apply = 1,
        Regenerate = 2,
    }

    public enum PatchRiskLevel
    {
        Unknown = 0,
        Safe = 1,
        Caution = 2,
        Dangerous = 3,
    }

    public sealed class PatchReviewInfo
    {
        public bool AiEnabled { get; init; }
        public bool AiUsed { get; init; }
        public bool AiSucceeded { get; init; }
        public bool? MatchesPrompt { get; init; }
        public string PromptSummary { get; init; } = string.Empty;
        public PatchRiskLevel RiskLevel { get; init; } = PatchRiskLevel.Unknown;
        public string RiskSummary { get; init; } = string.Empty;
        public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> SuggestedSafetyImprovements { get; init; } = Array.Empty<string>();
        public string? ErrorMessage { get; init; }
    }

    internal sealed class PatchReviewAiResponse
    {
        [JsonProperty("MatchesPrompt")]
        public bool MatchesPrompt { get; set; }

        [JsonProperty("PromptSummary")]
        public string PromptSummary { get; set; } = string.Empty;

        [JsonProperty("RiskLevel")]
        public string RiskLevel { get; set; } = "unknown";

        [JsonProperty("RiskSummary")]
        public string RiskSummary { get; set; } = string.Empty;

        [JsonProperty("Findings")]
        public List<string> Findings { get; set; } = new();

        [JsonProperty("SuggestedSafetyImprovements")]
        public List<string> SuggestedSafetyImprovements { get; set; } = new();
    }

    public static class PatchReviewService
    {
        private sealed record HeuristicRule(PatchRiskLevel Level, Regex Pattern, string Finding);

        private static readonly RegexOptions HeuristicRegexOptions =
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        private static readonly HeuristicRule[] HeuristicRules = new[]
        {
            new HeuristicRule(PatchRiskLevel.Dangerous, new Regex(@"\b(File|Directory)\.Delete\s*\(", HeuristicRegexOptions), "Deletes files or directories."),
            new HeuristicRule(PatchRiskLevel.Caution, new Regex(@"\b(File|Directory)\.Move\s*\(", HeuristicRegexOptions), "Moves files or directories."),
            new HeuristicRule(PatchRiskLevel.Caution, new Regex(@"\bFile\.Write(AllText|AllBytes|AllLines)\s*\(", HeuristicRegexOptions), "Writes files."),
            new HeuristicRule(PatchRiskLevel.Caution, new Regex(@"\bFile\.Append(AllText|AllLines)\s*\(", HeuristicRegexOptions), "Appends to files."),
            new HeuristicRule(PatchRiskLevel.Caution, new Regex(@"\bFile\.Read(AllText|AllBytes|AllLines)\s*\(", HeuristicRegexOptions), "Reads files."),
            new HeuristicRule(PatchRiskLevel.Caution, new Regex(@"\bDirectory\.(Enumerate|Get)(Files|Directories)\s*\(", HeuristicRegexOptions), "Enumerates files or directories."),
            new HeuristicRule(PatchRiskLevel.Dangerous, new Regex(@"\bProcess\.Start\s*\(", HeuristicRegexOptions), "Starts external processes."),
            new HeuristicRule(PatchRiskLevel.Dangerous, new Regex(@"\b(cmd\.exe|powershell(\.exe)?)\b", HeuristicRegexOptions), "Invokes a system shell (cmd/powershell)."),
            new HeuristicRule(PatchRiskLevel.Caution, new Regex(@"\bHttpClient\b|\bWebClient\b|\bHttpWebRequest\b|\bSocket\b|\bTcpClient\b|\bUdpClient\b", HeuristicRegexOptions), "Uses network APIs."),
            new HeuristicRule(PatchRiskLevel.Dangerous, new Regex(@"\bDllImport\b|\bMarshal\.", HeuristicRegexOptions), "Uses native interop (P/Invoke / Marshal)."),
            new HeuristicRule(PatchRiskLevel.Dangerous, new Regex(@"\bMicrosoft\.Win32\.Registry\b|\bRegistry(Key)?\b", HeuristicRegexOptions), "Accesses the Windows Registry."),
            new HeuristicRule(PatchRiskLevel.Caution, new Regex(@"\bEnvironment\.GetEnvironmentVariable(s)?\b", HeuristicRegexOptions), "Reads environment variables."),
            new HeuristicRule(PatchRiskLevel.Caution, new Regex(@"\bEnvironment\.GetFolderPath\b|\bSpecialFolder\b", HeuristicRegexOptions), "Accesses user/system folders."),
            new HeuristicRule(PatchRiskLevel.Caution, new Regex(@"\bAssembly\.Load\b|\bAppDomain\b|\bActivator\.CreateInstance\b|\bReflection\b", HeuristicRegexOptions), "Uses dynamic loading/reflection."),
        };

        public static async Task<PatchReviewInfo> ReviewAsync(
            IAgent? agent,
            string userPrompt,
            string patch,
            string resultingCode,
            IReadOnlyList<string>? nugetPackages,
            string? explanation,
            CancellationToken cancellationToken)
        {
            var (heuristicLevel, heuristicFindings) = RunHeuristicScan(resultingCode);

            bool aiUsed = agent != null;
            bool aiSucceeded = false;
            PatchReviewAiResponse? ai = null;
            string? aiError = null;

            if (agent != null)
            {
                try
                {
                    string prompt = BuildPatchReviewPrompt(
                        userPrompt: userPrompt,
                        patch: patch,
                        resultingCode: resultingCode,
                        nugetPackages: nugetPackages,
                        explanation: explanation,
                        heuristicFindings: heuristicFindings);

                    agent.SetInput("SystemMessage", AISystemMessages.GetPatchReviewerSystemMessage());
                    agent.SetInput("Prompt", prompt);
                    agent.SetInput("History", "");
                    agent.SetInput("JsonSchema", JsonSchemata.JsonSchemaPatchReviewResponse);

                    agent.SetOption("Temperature", 0.1f);
                    agent.SetOption("TopP", 0.9f);

                    await agent.ExecuteAsync(cancellationToken);

                    string raw = agent.GetOutput<string>("Result") ?? string.Empty;
                    if (!TryParseAiResponse(raw, out ai, out var parseError))
                    {
                        aiError = parseError;
                        aiSucceeded = false;
                        ai = null;
                    }
                    else
                    {
                        aiSucceeded = true;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    aiError = ex.Message;
                    aiSucceeded = false;
                    ai = null;
                }
            }

            PatchRiskLevel aiLevel = ParseRiskLevel(ai?.RiskLevel);
            PatchRiskLevel finalLevel = MaxRisk(heuristicLevel, aiLevel);

            var combinedFindings = new List<string>();
            AddUniqueFindings(combinedFindings, heuristicFindings);
            AddUniqueFindings(combinedFindings, ai?.Findings);

            var suggestedFixes = new List<string>();
            AddUniqueFindings(suggestedFixes, ai?.SuggestedSafetyImprovements);

            string riskSummary = !string.IsNullOrWhiteSpace(ai?.RiskSummary)
                ? ai!.RiskSummary.Trim()
                : BuildHeuristicSummary(finalLevel, heuristicFindings);

            string promptSummary = !string.IsNullOrWhiteSpace(ai?.PromptSummary)
                ? ai!.PromptSummary.Trim()
                : string.Empty;

            return new PatchReviewInfo
            {
                AiEnabled = true,
                AiUsed = aiUsed,
                AiSucceeded = aiSucceeded,
                MatchesPrompt = aiSucceeded ? ai!.MatchesPrompt : null,
                PromptSummary = promptSummary,
                RiskLevel = finalLevel,
                RiskSummary = riskSummary,
                Findings = combinedFindings,
                SuggestedSafetyImprovements = suggestedFixes,
                ErrorMessage = aiError,
            };
        }

        public static string BuildRegenerationInstruction(PatchReviewInfo reviewInfo)
        {
            if (reviewInfo == null) throw new ArgumentNullException(nameof(reviewInfo));

            var sb = new StringBuilder();

            sb.AppendLine("The previous proposal was flagged during patch review. Please regenerate a safer revision while still fulfilling the user's intent.");

            if (reviewInfo.MatchesPrompt == false)
                sb.AppendLine("It also did not fully match the user's request.");

            if (reviewInfo.RiskLevel != PatchRiskLevel.Unknown)
                sb.AppendLine($"Assessed risk level: {reviewInfo.RiskLevel}.");

            var findings = (reviewInfo.Findings ?? Array.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).Take(8).ToList();
            if (findings.Count > 0)
            {
                sb.AppendLine("Avoid or safety-gate these behaviors:");
                foreach (var f in findings)
                    sb.AppendLine("- " + f.Trim());
            }

            var fixes = (reviewInfo.SuggestedSafetyImprovements ?? Array.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).Take(8).ToList();
            if (fixes.Count > 0)
            {
                sb.AppendLine("Apply these safety improvements:");
                foreach (var f in fixes)
                    sb.AppendLine("- " + f.Trim());
            }

            sb.AppendLine("Safety requirements:");
            sb.AppendLine("- Do not perform destructive actions automatically (delete/overwrite).");
            sb.AppendLine("- Require explicit user confirmation for any risky action.");
            sb.AppendLine("- Restrict scope to user-selected paths; never operate on broad folders by default.");
            sb.AppendLine("- Avoid executing external processes or changing system settings unless explicitly requested and confirmed.");
            sb.AppendLine("- Avoid network access unless explicitly requested; never exfiltrate user data.");
            sb.AppendLine("Return a valid unified diff patch targeting './currentcode.cs'.");

            return sb.ToString();
        }

        private static (PatchRiskLevel Level, List<string> Findings) RunHeuristicScan(string code)
        {
            var findings = new List<string>();
            PatchRiskLevel level = PatchRiskLevel.Safe;

            if (string.IsNullOrWhiteSpace(code))
                return (PatchRiskLevel.Unknown, findings);

            foreach (var rule in HeuristicRules)
            {
                if (!rule.Pattern.IsMatch(code))
                    continue;

                findings.Add(rule.Finding);
                level = MaxRisk(level, rule.Level);
            }

            if (findings.Count == 0)
                level = PatchRiskLevel.Safe;

            return (level, findings);
        }

        private static void AddUniqueFindings(List<string> destination, IEnumerable<string>? findings)
        {
            if (findings == null) return;

            var set = new HashSet<string>(destination, StringComparer.OrdinalIgnoreCase);
            foreach (var f in findings)
            {
                var trimmed = (f ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;
                if (set.Add(trimmed))
                    destination.Add(trimmed);
            }
        }

        private static PatchRiskLevel MaxRisk(PatchRiskLevel a, PatchRiskLevel b)
            => (PatchRiskLevel)Math.Max((int)a, (int)b);

        private static PatchRiskLevel ParseRiskLevel(string? riskLevel)
        {
            if (string.IsNullOrWhiteSpace(riskLevel))
                return PatchRiskLevel.Unknown;

            return riskLevel.Trim().ToLowerInvariant() switch
            {
                "safe" => PatchRiskLevel.Safe,
                "caution" => PatchRiskLevel.Caution,
                "dangerous" => PatchRiskLevel.Dangerous,
                "unknown" => PatchRiskLevel.Unknown,
                _ => PatchRiskLevel.Unknown,
            };
        }

        private static string BuildHeuristicSummary(PatchRiskLevel riskLevel, List<string> heuristicFindings)
        {
            if (riskLevel == PatchRiskLevel.Safe)
                return "No obvious risky operations detected by heuristic scan.";

            if (riskLevel == PatchRiskLevel.Unknown)
                return "Heuristic scan could not assess risk.";

            if (heuristicFindings == null || heuristicFindings.Count == 0)
                return "Potentially risky behavior detected.";

            var unique = heuristicFindings.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return $"Heuristic scan flagged: {string.Join(" ", unique)}";
        }

        private static string BuildPatchReviewPrompt(
            string userPrompt,
            string patch,
            string resultingCode,
            IReadOnlyList<string>? nugetPackages,
            string? explanation,
            List<string> heuristicFindings)
        {
            var packs = (nugetPackages ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();

            // Keep payload bounded in case the code is very large.
            const int maxCodeChars = 16000;
            string codeForReview = resultingCode ?? string.Empty;
            if (codeForReview.Length > maxCodeChars)
                codeForReview = codeForReview.Substring(0, maxCodeChars) + "\n\n/* TRUNCATED */";

            var sb = new StringBuilder();
            sb.AppendLine("Review the following proposal.");
            sb.AppendLine();
            sb.AppendLine("USER_PROMPT:");
            sb.AppendLine(userPrompt ?? string.Empty);
            sb.AppendLine();
            sb.AppendLine("GENERATOR_EXPLANATION (may be empty):");
            sb.AppendLine(explanation ?? string.Empty);
            sb.AppendLine();
            sb.AppendLine("NUGET_PACKAGES:");
            if (packs.Count == 0) sb.AppendLine("(none)");
            foreach (var p in packs) sb.AppendLine("- " + p);
            sb.AppendLine();
            sb.AppendLine("UNIFIED_DIFF_PATCH (targets ./currentcode.cs):");
            sb.AppendLine(patch ?? string.Empty);
            sb.AppendLine();
            sb.AppendLine("RESULTING_CODE (./currentcode.cs):");
            sb.AppendLine(codeForReview);
            sb.AppendLine();
            sb.AppendLine("HEURISTIC_FLAGS:");
            if (heuristicFindings == null || heuristicFindings.Count == 0) sb.AppendLine("(none)");
            foreach (var f in (heuristicFindings ?? new List<string>()).Take(12)) sb.AppendLine("- " + f);
            sb.AppendLine();
            sb.AppendLine("Return JSON only.");
            return sb.ToString();
        }

        private static bool TryParseAiResponse(string raw, out PatchReviewAiResponse? response, out string? errorMessage)
        {
            response = null;
            errorMessage = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                errorMessage = "AI reviewer returned an empty response.";
                return false;
            }

            string json = ExtractFirstJsonObject(raw);
            if (string.IsNullOrWhiteSpace(json))
            {
                errorMessage = "AI reviewer did not return valid JSON.";
                return false;
            }

            try
            {
                var obj = JObject.Parse(json);

                var parsed = new PatchReviewAiResponse
                {
                    MatchesPrompt = ReadBool(obj["MatchesPrompt"], defaultValue: false),
                    PromptSummary = ReadString(obj["PromptSummary"], defaultValue: string.Empty),
                    RiskLevel = ReadString(obj["RiskLevel"], defaultValue: "unknown"),
                    RiskSummary = ReadString(obj["RiskSummary"], defaultValue: string.Empty),
                    Findings = ReadStringList(obj["Findings"]),
                    SuggestedSafetyImprovements = ReadStringList(obj["SuggestedSafetyImprovements"]),
                };

                response = parsed;
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ShortenErrorMessage("AI reviewer returned invalid JSON.", ex.Message);
                return false;
            }
        }

        private static string ExtractFirstJsonObject(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
                return string.Empty;

            return text.Substring(start, end - start + 1).Trim();
        }

        private static string ShortenErrorMessage(string prefix, string? details)
        {
            var d = (details ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(d))
                return prefix;

            const int maxLen = 420;
            if (d.Length > maxLen)
                d = d.Substring(0, maxLen) + "...";

            return prefix + " " + d;
        }

        private static bool ReadBool(JToken? token, bool defaultValue)
        {
            if (token == null)
                return defaultValue;

            try
            {
                if (token.Type == JTokenType.Boolean)
                    return token.Value<bool>();

                if (token.Type == JTokenType.String && bool.TryParse(token.Value<string>(), out bool b))
                    return b;

                if (token.Type == JTokenType.Integer && int.TryParse(token.ToString(), out int i))
                    return i != 0;
            }
            catch
            {
                // Ignore and return default.
            }

            return defaultValue;
        }

        private static string ReadString(JToken? token, string defaultValue)
        {
            if (token == null)
                return defaultValue;

            var s = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
            return string.IsNullOrWhiteSpace(s) ? defaultValue : s.Trim();
        }

        private static List<string> ReadStringList(JToken? token)
        {
            var list = new List<string>();
            if (token == null)
                return list;

            if (token.Type == JTokenType.Array)
            {
                foreach (var item in token.Values<JToken>())
                {
                    var s = item!.Type == JTokenType.String ? item.Value<string>() : item.ToString();
                    s = (s ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(s))
                        list.Add(s);
                }
                return list;
            }

            if (token.Type == JTokenType.String)
            {
                var s = (token.Value<string>() ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(s))
                    return list;

                // If the model accidentally wrapped an array inside a string (sometimes even with extra prefixes),
                // try to recover by parsing the first bracketed JSON array.
                int a = s.IndexOf('[');
                int b = s.LastIndexOf(']');
                if (a >= 0 && b > a)
                {
                    string candidate = s.Substring(a, b - a + 1);
                    try
                    {
                        var arr = JArray.Parse(candidate);
                        foreach (var item in arr.Values<JToken>())
                        {
                            var itemStr = item!.Type == JTokenType.String ? item.Value<string>() : item.ToString();
                            itemStr = (itemStr ?? string.Empty).Trim();
                            if (!string.IsNullOrWhiteSpace(itemStr))
                                list.Add(itemStr);
                        }
                        return list;
                    }
                    catch
                    {
                        // Fall through.
                    }
                }

                // Try to interpret as newline-separated bullets.
                var lines = s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim().TrimStart('-', '•', '*', ' ')).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                if (lines.Count > 0)
                    return lines;

                list.Add(s);
                return list;
            }

            // Fallback: treat as single string item.
            var fallback = token.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(fallback))
                list.Add(fallback);

            return list;
        }
    }
}

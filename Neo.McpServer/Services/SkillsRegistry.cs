using System.Text.Json;

namespace Neo.McpServer.Services;

/// <summary>
/// Manages a central registry of saved app "skills" — sessions that Claude can
/// automatically recognize and load across conversations.
/// Reads/writes skills.json from the NEO_SKILLS_PATH directory.
/// </summary>
public sealed class SkillsRegistry
{
    private readonly string? _skillsDir;
    private readonly string? _skillsFilePath;

    public record Skill(
        string Name,
        string Description,
        string[] Keywords,
        string SessionPath,
        string CreatedAt,
        string? LastUsed = null);

    private record SkillsFile(int Version, List<Skill> Skills);

    public bool IsConfigured => _skillsDir != null;
    public string? SkillsDirectory => _skillsDir;

    public SkillsRegistry()
    {
        _skillsDir = Environment.GetEnvironmentVariable("NEO_SKILLS_PATH");
        if (!string.IsNullOrWhiteSpace(_skillsDir))
        {
            _skillsFilePath = Path.Combine(_skillsDir, "skills.json");
            try { Directory.CreateDirectory(_skillsDir); } catch { }
        }
    }

    public List<Skill> GetSkills()
    {
        if (_skillsFilePath == null || !File.Exists(_skillsFilePath))
            return new List<Skill>();

        try
        {
            var json = File.ReadAllText(_skillsFilePath);
            var file = JsonSerializer.Deserialize<SkillsFile>(json, JsonOpts);
            return file?.Skills ?? new List<Skill>();
        }
        catch
        {
            return new List<Skill>();
        }
    }

    public (bool Success, string Message) Register(string name, string description, string keywords, string sessionPath)
    {
        if (!IsConfigured)
            return (false, "NEO_SKILLS_PATH is not set. Add it to your MCP server env config.");

        if (string.IsNullOrWhiteSpace(name))
            return (false, "Skill name cannot be empty.");
        if (string.IsNullOrWhiteSpace(sessionPath))
            return (false, "Session path cannot be empty.");
        if (!File.Exists(sessionPath))
            return (false, $"Session file not found: {sessionPath}");

        var keywordArray = keywords
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToArray();

        var skills = GetSkills();

        // Update existing or add new
        var existing = skills.FindIndex(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        var skill = new Skill(
            Name: name,
            Description: description,
            Keywords: keywordArray,
            SessionPath: sessionPath,
            CreatedAt: existing >= 0 ? skills[existing].CreatedAt : DateTime.UtcNow.ToString("o"),
            LastUsed: null);

        if (existing >= 0)
            skills[existing] = skill;
        else
            skills.Add(skill);

        return SaveSkills(skills)
            ? (true, $"Skill '{name}' registered with {keywordArray.Length} keywords.")
            : (false, "Failed to write skills.json.");
    }

    public (bool Success, string Message) Unregister(string name)
    {
        if (!IsConfigured)
            return (false, "NEO_SKILLS_PATH is not set.");

        var skills = GetSkills();
        var removed = skills.RemoveAll(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        if (removed == 0)
            return (false, $"Skill '{name}' not found.");

        return SaveSkills(skills)
            ? (true, $"Skill '{name}' removed.")
            : (false, "Failed to write skills.json.");
    }

    public void MarkUsed(string name)
    {
        if (!IsConfigured) return;

        var skills = GetSkills();
        var idx = skills.FindIndex(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        if (idx >= 0)
        {
            var s = skills[idx];
            skills[idx] = s with { LastUsed = DateTime.UtcNow.ToString("o") };
            SaveSkills(skills);
        }
    }

    /// <summary>
    /// Returns a formatted string for injection into the MCP prompt.
    /// Empty string if no skills are registered.
    /// </summary>
    public string GetSkillsPromptSection()
    {
        var skills = GetSkills();
        if (skills.Count == 0) return "";

        var lines = new List<string>
        {
            "",
            "## Available App Skills",
            "",
            "Before generating new code, check if the user's request matches an existing skill.",
            "If it does, use `load_session` with the session path instead of writing new code.",
            "",
            "| Skill | Description | Keywords | Load Command |",
            "|-------|-------------|----------|-------------|"
        };

        foreach (var skill in skills)
        {
            var kw = string.Join(", ", skill.Keywords);
            lines.Add($"| {skill.Name} | {skill.Description} | {kw} | `load_session(path: \"{skill.SessionPath.Replace("\\", "/")}\")`  |");
        }

        lines.Add("");
        lines.Add("If no skill matches, generate new code as usual.");

        return string.Join("\n", lines);
    }

    private bool SaveSkills(List<Skill> skills)
    {
        if (_skillsFilePath == null) return false;

        try
        {
            var file = new SkillsFile(1, skills);
            var json = JsonSerializer.Serialize(file, JsonOpts);
            File.WriteAllText(_skillsFilePath, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}

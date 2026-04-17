using System;
using System.Text.Json;

namespace Neo.AssemblyForge;

public static class StructuredResponseParser
{
    /// <summary>
    /// Extracts the StructuredResponse object from the AI's raw reply.
    /// Accepts: plain JSON, JSON inside a ```json fence, or JSON embedded
    /// anywhere in prose (first {…} run).
    /// </summary>
    public static StructuredResponse? Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var json = ExtractJson(raw);
        if (json is null) return null;

        try
        {
            return JsonSerializer.Deserialize<StructuredResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractJson(string raw)
    {
        // 1) ```json { … } ```
        var fenceStart = raw.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (fenceStart >= 0)
        {
            var jsonStart = raw.IndexOf('{', fenceStart);
            var fenceEnd = raw.IndexOf("```", jsonStart);
            if (jsonStart >= 0 && fenceEnd > jsonStart)
                return raw.Substring(jsonStart, fenceEnd - jsonStart);
        }

        // 2) plain or embedded — grab the first balanced { … }
        var open = raw.IndexOf('{');
        if (open < 0) return null;

        int depth = 0;
        bool inString = false;
        bool escape = false;
        for (int i = open; i < raw.Length; i++)
        {
            var c = raw[i];
            if (escape) { escape = false; continue; }
            if (c == '\\') { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return raw.Substring(open, i - open + 1);
            }
        }
        return null;
    }
}

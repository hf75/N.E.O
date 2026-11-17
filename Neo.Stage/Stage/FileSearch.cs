using System.Data.OleDb;
using System.Globalization;

namespace Neo.Stage;

/// <summary>A file the Windows Search index knows.</summary>
internal sealed record FoundFile(string Path, DateTime Modified);

/// <summary>
/// Finds files in the person's own folders by name and content, through the Windows Search index (the same one the
/// Explorer searches). Only read; the program folders and AppData are left out.
/// </summary>
internal static class FileSearch
{
    public static IReadOnlyList<FoundFile> Find(string words, int most = 8)
    {
        // Words become prefixes ("netzwerkplan*"), all of which must be found; signs that would break the query are dropped.
        var terms = words.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string([.. word.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.')]).Trim('.'))
            .Where(word => word.Length > 1)
            .Take(5)
            .ToList();
        if (terms.Count == 0)
            return [];
        var wanted = string.Join(" AND ", terms.Select(term => $"\"{term}*\""));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/');
        var query = $"SELECT TOP {most} System.ItemPathDisplay, System.DateModified FROM SystemIndex " +
                    $"WHERE SCOPE='file:{profile}' AND System.Kind <> 'folder' AND NOT System.ItemPathDisplay LIKE '%\\AppData\\%' " +
                    $"AND (CONTAINS(System.FileName, '{wanted}') OR CONTAINS(*, '{wanted}')) " +
                    "ORDER BY System.Search.Rank DESC";

        var found = new List<FoundFile>();
        using var connection = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
        connection.Open();
        using var command = new OleDbCommand(query, connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
                continue;
            var modified = reader.IsDBNull(1) ? DateTime.MinValue : Convert.ToDateTime(reader.GetValue(1), CultureInfo.InvariantCulture).ToLocalTime();
            found.Add(new FoundFile(reader.GetString(0), modified));
        }
        return found;
    }
}

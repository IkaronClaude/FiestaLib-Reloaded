namespace FiestaLibReloaded.Config;

public static class ServerInfoParser
{
    public static List<ServerInfoEntry> Parse(string filePath)
    {
        var entries = new List<ServerInfoEntry>();
        var inDefineBlock = false;

        foreach (var rawLine in File.ReadLines(filePath))
        {
            var line = rawLine.Trim();

            if (line.StartsWith("#DEFINE", StringComparison.OrdinalIgnoreCase))
            {
                inDefineBlock = true;
                continue;
            }
            if (line.StartsWith("#ENDDEFINE", StringComparison.OrdinalIgnoreCase))
            {
                inDefineBlock = false;
                continue;
            }
            if (inDefineBlock) continue;

            // Skip comments, empty lines, and non-SERVER_INFO directives
            if (string.IsNullOrEmpty(line) || line.StartsWith("//") || line.StartsWith("#"))
                continue;
            if (!line.StartsWith("SERVER_INFO", StringComparison.OrdinalIgnoreCase))
                continue;

            // Strip "SERVER_INFO" prefix and parse CSV
            var csv = line["SERVER_INFO".Length..].Trim();
            var parts = SplitCsv(csv);

            if (parts.Count < 9)
                continue;

            entries.Add(new ServerInfoEntry(
                Name: Unquote(parts[0]),
                ServerType: int.Parse(parts[1]),
                WorldNum: int.Parse(parts[2]),
                ZoneNum: int.Parse(parts[3]),
                FromServerType: int.Parse(parts[4]),
                IpAddress: Unquote(parts[5]),
                Port: int.Parse(parts[6]),
                MaxConnections: int.Parse(parts[7]),
                UserLimit: int.Parse(parts[8])));
        }

        return entries;
    }

    public static List<ServerInfoEntry> GetOpToolEndpoints(string filePath)
        => Parse(filePath)
            .Where(e => e.FromServerType == (int)FiestaServerType.OpTool)
            .ToList();

    private static List<string> SplitCsv(string csv)
    {
        var parts = new List<string>();
        var inQuotes = false;
        var current = new System.Text.StringBuilder();

        foreach (var ch in csv)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                current.Append(ch);
            }
            else if (ch == ',' && !inQuotes)
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            parts.Add(current.ToString().Trim());

        return parts;
    }

    private static string Unquote(string s)
        => s.Length >= 2 && s[0] == '"' && s[^1] == '"'
            ? s[1..^1]
            : s;
}

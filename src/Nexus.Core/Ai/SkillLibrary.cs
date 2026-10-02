namespace Nexus.Core.Ai;

/// <summary>An agent skill: a folder with SKILL.md (name and description in its YAML front matter).</summary>
/// <param name="Source">Who uses it: "Claude Code", "Плагин «…»", "Codex".</param>
public sealed record AiSkill(string Name, string Description, string Folder, string Source);

/// <summary>Skills installed for Claude Code (own and from plugins) and for Codex.</summary>
public static class SkillLibrary
{
    public const string SkillFileName = "SKILL.md";

    public static IReadOnlyList<AiSkill> Load(string userProfile)
    {
        var skills = new List<AiSkill>();
        AddSkills(skills, Path.Combine(userProfile, ".claude", "skills"), "Claude Code");

        // Installed plugins live in plugins\cache\{marketplace}\{plugin}\{version}; only the newest version counts.
        foreach (var marketplace in SafeDirectories(Path.Combine(userProfile, ".claude", "plugins", "cache")))
        {
            foreach (var plugin in SafeDirectories(marketplace))
            {
                if (NewestVersion(plugin) is { } version)
                {
                    AddSkills(skills, Path.Combine(version, "skills"), $"Плагин «{Path.GetFileName(plugin)}»");
                }
            }
        }

        var codex = Path.Combine(userProfile, ".codex", "skills");
        AddSkills(skills, codex, "Codex");
        AddSkills(skills, Path.Combine(codex, ".system"), "Codex (встроенный)");

        return skills
            .DistinctBy(skill => skill.Folder, StringComparer.OrdinalIgnoreCase)
            .OrderBy(skill => skill.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>Reads a skill folder; null when it has no SKILL.md.</summary>
    public static AiSkill? Read(string folder, string source)
    {
        var file = Path.Combine(folder, SkillFileName);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            var (name, description) = ParseFrontMatter(File.ReadLines(file).Take(80).ToArray());
            return new AiSkill(name ?? Path.GetFileName(folder), description ?? string.Empty, folder, source);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// "name:" and "description:" from the YAML front matter, including folded (">") and literal ("|")
    /// multi-line values; the first text line stands in for a missing description.
    /// </summary>
    public static (string? Name, string? Description) ParseFrontMatter(IReadOnlyList<string> lines)
    {
        string? name = null;
        string? description = null;
        var index = 0;
        if (lines.Count > 0 && lines[0].Trim() == "---")
        {
            for (index = 1; index < lines.Count && lines[index].Trim() != "---"; index++)
            {
                var line = lines[index];
                if (char.IsWhiteSpace(line.FirstOrDefault()) || !line.Contains(':', StringComparison.Ordinal))
                {
                    continue;
                }

                var key = line[..line.IndexOf(':', StringComparison.Ordinal)].Trim();
                var value = line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
                if (value is ">" or "|" or ">-" or "|-" or ">+" or "|+")
                {
                    var block = new List<string>();
                    while (index + 1 < lines.Count && lines[index + 1].Length > 0 && char.IsWhiteSpace(lines[index + 1][0]))
                    {
                        block.Add(lines[++index].Trim());
                    }

                    value = string.Join(' ', block);
                }

                value = value.Trim('"', '\'');
                if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    name = value;
                }
                else if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
                {
                    description = value;
                }
            }

            index++;
        }

        description ??= lines.Skip(index)
            .Select(line => line.Trim().TrimStart('#').Trim())
            .FirstOrDefault(line => line.Length > 0);
        return (string.IsNullOrWhiteSpace(name) ? null : name, string.IsNullOrWhiteSpace(description) ? null : description);
    }

    private static void AddSkills(List<AiSkill> skills, string folder, string source)
    {
        foreach (var skillFolder in SafeDirectories(folder))
        {
            if (Read(skillFolder, source) is { } skill)
            {
                skills.Add(skill);
            }
        }
    }

    private static string? NewestVersion(string plugin) =>
        SafeDirectories(plugin)
            .OrderByDescending(folder => Version.TryParse(Path.GetFileName(folder), out var version) ? version : new Version(0, 0))
            .ThenByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();

    private static string[] SafeDirectories(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetDirectories(folder) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

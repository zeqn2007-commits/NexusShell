using System.IO.Compression;

namespace Nexus.Core.Ai;

/// <summary>Installs a downloaded skill for Claude Code, whose personal skills live in ~/.claude/skills.</summary>
public static class SkillInstaller
{
    public static string ClaudeSkillsFolder(string userProfile) => Path.Combine(userProfile, ".claude", "skills");

    /// <summary>
    /// The folder to copy into the skills folder: a skill folder as it is, or the contents of a
    /// .skill / .zip archive extracted under <paramref name="workFolder"/> (SKILL.md at the archive's
    /// root or in its single top folder).
    /// </summary>
    public static string PrepareSource(string path, string workFolder)
    {
        if (Directory.Exists(path))
        {
            return File.Exists(Path.Combine(path, SkillLibrary.SkillFileName))
                ? path
                : throw new InvalidDataException("В папке нет SKILL.md — это не скилл.");
        }

        // ExtractToDirectory refuses entries that would land outside the target folder.
        var target = Path.Combine(workFolder, Path.GetFileNameWithoutExtension(path));
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        ZipFile.ExtractToDirectory(path, target);
        if (File.Exists(Path.Combine(target, SkillLibrary.SkillFileName)))
        {
            return target;
        }

        return Directory.GetDirectories(target).FirstOrDefault(folder => File.Exists(Path.Combine(folder, SkillLibrary.SkillFileName)))
            ?? throw new InvalidDataException("В архиве нет SKILL.md — это не скилл.");
    }
}

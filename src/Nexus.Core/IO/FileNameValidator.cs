namespace Nexus.Core.IO;

/// <summary>Windows file name rules with messages ready for the UI.</summary>
public static class FileNameValidator
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <returns><c>null</c> when the name is valid, otherwise a message.</returns>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Введите имя.";
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "Имя файла не должно содержать символы: \\ / : * ? \" < > |";
        }

        if (name.EndsWith('.') || name.EndsWith(' ') || name.StartsWith(' '))
        {
            return "Имя не может начинаться с пробела или заканчиваться точкой или пробелом.";
        }

        if (name.Length > 255)
        {
            return "Имя слишком длинное (больше 255 символов).";
        }

        var stem = name.Split('.')[0];
        return ReservedNames.Contains(stem) ? $"Имя «{stem}» зарезервировано Windows." : null;
    }
}

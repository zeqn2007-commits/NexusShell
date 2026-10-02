using Nexus.Core.Interop;

namespace Nexus.Core.Shell;

/// <summary>The app Windows opens a file type with.</summary>
public sealed record DefaultApp(string Name, string Executable);

public static class FileAssociations
{
    private const int AssocFlagInitIgnoreUnknown = 0x400;
    private const int AssocStringExecutable = 2;
    private const int AssocStringFriendlyAppName = 4;

    /// <summary>The default app for an extension such as ".torrent"; null when none is chosen.</summary>
    public static DefaultApp? GetDefaultApp(string extension)
    {
        var executable = Query(AssocStringExecutable, extension);
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable)
            || string.Equals(Path.GetFileName(executable), "OpenWith.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = Query(AssocStringFriendlyAppName, extension);
        return new DefaultApp(string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(executable) : name, executable);
    }

    private static string? Query(int what, string extension)
    {
        uint length = 0;
        var result = NativeMethods.AssocQueryStringW(AssocFlagInitIgnoreUnknown, what, extension, "open", null, ref length);
        if ((result != NativeMethods.S_OK && result != NativeMethods.S_FALSE) || length == 0)
        {
            return null;
        }

        var buffer = new char[length];
        result = NativeMethods.AssocQueryStringW(AssocFlagInitIgnoreUnknown, what, extension, "open", buffer, ref length);
        return result == NativeMethods.S_OK ? new string(buffer, 0, (int)Math.Max(0, length - 1)) : null;
    }
}

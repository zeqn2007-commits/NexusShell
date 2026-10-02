using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Nexus.Core.Interop;

namespace Nexus.Core.Shell;

public sealed record ShortcutInfo(string? TargetPath, string? Arguments, string? WorkingDirectory, string? IconPath, int IconIndex);

/// <summary>Reads .lnk shortcuts. Must be called on an STA thread.</summary>
public static class ShellLinks
{
    private const uint RawPath = 0x4;

    public static ShortcutInfo? TryRead(string shortcutPath)
    {
        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLinkCoClass();
            ((IPersistFile)link).Load(shortcutPath, 0);

            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, RawPath);
            var arguments = new StringBuilder(2048);
            link.GetArguments(arguments, arguments.Capacity);
            var directory = new StringBuilder(1024);
            link.GetWorkingDirectory(directory, directory.Capacity);
            var icon = new StringBuilder(1024);
            link.GetIconLocation(icon, icon.Capacity, out var iconIndex);

            return new ShortcutInfo(
                Expand(target.ToString()),
                NullIfEmpty(arguments.ToString()),
                Expand(directory.ToString()),
                Expand(icon.ToString()),
                iconIndex);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        finally
        {
            NativeMethods.Release(link);
        }
    }

    private static string? Expand(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : Environment.ExpandEnvironmentVariables(value.Trim());

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

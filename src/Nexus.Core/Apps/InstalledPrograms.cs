using System.Security;
using Microsoft.Win32;

namespace Nexus.Core.Apps;

/// <summary>Desktop programs registered for "Apps &amp; features": the DisplayName of every Uninstall entry.</summary>
public static class InstalledPrograms
{
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private const string Wow64UninstallKey = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Machine-wide (64- and 32-bit) and per-user programs.</summary>
    public static IReadOnlyList<string> Load() =>
    [
        .. Read(Registry.LocalMachine, UninstallKey)
            .Concat(Read(Registry.LocalMachine, Wow64UninstallKey))
            .Concat(Read(Registry.CurrentUser, UninstallKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
    ];

    public static IReadOnlyList<string> Read(RegistryKey root, string path)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key is null)
            {
                return [];
            }

            var names = new List<string>();
            foreach (var name in key.GetSubKeyNames())
            {
                using var entry = key.OpenSubKey(name);
                if (entry?.GetValue("DisplayName") is string display && !string.IsNullOrWhiteSpace(display))
                {
                    names.Add(display.Trim());
                }
            }

            return names;
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }
}

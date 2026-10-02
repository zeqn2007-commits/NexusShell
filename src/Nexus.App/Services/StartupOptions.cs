using Microsoft.UI.Xaml;

namespace Nexus.App.Services;

/// <summary>
/// Command-line options. <c>--page=games</c> opens a section on start,
/// <c>--theme=light|dark</c> overrides the system theme (used for screenshots).
/// </summary>
public sealed record StartupOptions(string? Page, ElementTheme Theme)
{
    public static StartupOptions Parse(IEnumerable<string> args)
    {
        string? page = null;
        var theme = ElementTheme.Default;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
            {
                page = arg["--page=".Length..].Trim();
            }
            else if (arg.Equals("--theme=light", StringComparison.OrdinalIgnoreCase))
            {
                theme = ElementTheme.Light;
            }
            else if (arg.Equals("--theme=dark", StringComparison.OrdinalIgnoreCase))
            {
                theme = ElementTheme.Dark;
            }
        }

        return new StartupOptions(page, theme);
    }
}

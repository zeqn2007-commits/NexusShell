namespace Nexus.Core.Integration;

public enum LaunchTarget
{
    Default,
    Folder,
    SelectItem,
    Section
}

/// <summary>What a command line asks Nexus to open (also used for redirected launches).</summary>
/// <param name="Target">Kind of request.</param>
/// <param name="Path">Folder to open, or the item to select inside its folder.</param>
/// <param name="Section">A Nexus section tag (home, thispc, recycle, games…).</param>
public sealed record LaunchRequest(LaunchTarget Target, string? Path = null, string? Section = null)
{
    private static readonly Dictionary<string, string> ShellFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"] = "thispc",
        ["::{645FF040-5081-101B-9F08-00AA002F954E}"] = "recycle",
        ["::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}"] = "network",
        ["::{679f85cb-0220-4080-b29b-5540cc05aab6}"] = "home",
        ["::{f874310e-b6b7-47dc-bc84-b9e6b38f5903}"] = "home",
        ["shell:RecycleBinFolder"] = "recycle",
        ["shell:MyComputerFolder"] = "thispc"
    };

    /// <summary>
    /// Understands what Explorer is usually given: a folder path, <c>/select,"file"</c>,
    /// <c>/e,</c> and <c>/root,</c> prefixes, shell folder CLSIDs and Nexus' own <c>--page=</c>.
    /// </summary>
    public static LaunchRequest Parse(IReadOnlyList<string> args)
    {
        var parts = args.Where(arg => !string.IsNullOrWhiteSpace(arg)).ToList();
        for (var index = 0; index < parts.Count; index++)
        {
            var arg = parts[index].Trim();
            if (arg.StartsWith("--page=", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (arg.StartsWith("/select,", StringComparison.OrdinalIgnoreCase) || arg.Equals("/select", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg.Length > "/select,".Length ? arg["/select,".Length..] : index + 1 < parts.Count ? parts[++index] : string.Empty;
                var item = Clean(value);
                if (item.Length > 0)
                {
                    return new LaunchRequest(LaunchTarget.SelectItem, item);
                }

                continue;
            }

            foreach (var prefix in new[] { "/e,", "/root,", "/n,", "/e", "/n" })
            {
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    arg = arg[prefix.Length..];
                    break;
                }
            }

            arg = Clean(arg);
            if (arg.Length == 0)
            {
                continue;
            }

            if (ShellFolders.TryGetValue(arg, out var section))
            {
                return new LaunchRequest(LaunchTarget.Section, Section: section);
            }

            var expanded = Environment.ExpandEnvironmentVariables(arg);
            if (IO.PathHelper.IsNetworkComputer(expanded))
            {
                // \\server is not a directory on its own; Nexus lists the computer's shared folders.
                return new LaunchRequest(LaunchTarget.Folder, $@"\\{expanded.Trim('\\')}");
            }

            if (Directory.Exists(expanded))
            {
                return new LaunchRequest(LaunchTarget.Folder, IO.PathHelper.Normalize(expanded));
            }

            if (File.Exists(expanded))
            {
                return new LaunchRequest(LaunchTarget.SelectItem, IO.PathHelper.Normalize(expanded));
            }
        }

        return new LaunchRequest(LaunchTarget.Default);
    }

    /// <summary>Splits a raw command-line string the way Windows does (quotes group spaces).</summary>
    public static IReadOnlyList<string> SplitCommandLine(string commandLine)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in commandLine)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static string Clean(string value) => value.Trim().Trim('"').Trim().TrimEnd(',');
}

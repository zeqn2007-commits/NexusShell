using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexus.Core.Ai;

/// <summary>An MCP server configured for an AI client.</summary>
/// <param name="Client">The app that starts it: "Claude Code", "Claude", "Codex", "Cursor"…</param>
/// <param name="Command">What it runs (secrets hidden), or the URL of a remote server.</param>
public sealed record McpServer(string Name, string Client, string Command, string ConfigPath);

/// <summary>
/// MCP servers from the config files of Claude Code, Claude Desktop, Codex, Cursor, VS Code,
/// Windsurf, Gemini CLI and LM Studio, plus a project's own .mcp.json.
/// </summary>
public static partial class McpConfigs
{
    private const string Masked = "••••";
    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private static readonly string[] SecretWords = ["key", "token", "secret", "password", "passwd", "auth", "bearer", "credential"];

    public static IReadOnlyList<McpServer> Load(string userProfile, string appData)
    {
        var servers = new List<McpServer>();

        // Claude Code keeps user servers at the top of ~/.claude.json and project servers under "projects".
        var claudeCode = Path.Combine(userProfile, ".claude.json");
        ReadJson(claudeCode, root =>
        {
            AddServers(servers, root, "mcpServers", "Claude Code", claudeCode);
            if (root.TryGetProperty("projects", out var projects) && projects.ValueKind == JsonValueKind.Object)
            {
                foreach (var project in projects.EnumerateObject().Where(project => project.Value.ValueKind == JsonValueKind.Object))
                {
                    AddServers(servers, project.Value, "mcpServers", "Claude Code", claudeCode);
                }
            }
        });

        AddFile(servers, Path.Combine(appData, "Claude", "claude_desktop_config.json"), "mcpServers", "Claude");
        AddFile(servers, Path.Combine(userProfile, ".cursor", "mcp.json"), "mcpServers", "Cursor");
        AddFile(servers, Path.Combine(appData, "Code", "User", "mcp.json"), "servers", "VS Code");
        var vsCodeSettings = Path.Combine(appData, "Code", "User", "settings.json");
        ReadJson(vsCodeSettings, root =>
        {
            if (root.TryGetProperty("mcp", out var mcp) && mcp.ValueKind == JsonValueKind.Object)
            {
                AddServers(servers, mcp, "servers", "VS Code", vsCodeSettings);
            }
        });
        AddFile(servers, Path.Combine(userProfile, ".codeium", "windsurf", "mcp_config.json"), "mcpServers", "Windsurf");
        AddFile(servers, Path.Combine(userProfile, ".gemini", "settings.json"), "mcpServers", "Gemini CLI");
        AddFile(servers, Path.Combine(userProfile, ".lmstudio", "mcp.json"), "mcpServers", "LM Studio");
        servers.AddRange(ReadCodex(Path.Combine(userProfile, ".codex", "config.toml")));

        return servers
            .DistinctBy(server => (server.Client, server.Name, server.Command))
            .OrderBy(server => server.Client, StringComparer.CurrentCulture)
            .ThenBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>Servers declared by a project in its own .mcp.json.</summary>
    public static IReadOnlyList<McpServer> LoadProject(string projectFolder)
    {
        var servers = new List<McpServer>();
        AddFile(servers, Path.Combine(projectFolder, ".mcp.json"), "mcpServers", $"Проект «{Path.GetFileName(projectFolder)}»");
        return servers;
    }

    /// <summary>True when a JSON file declares MCP servers (used to recognise downloaded configs).</summary>
    public static bool DeclaresServers(string jsonPath)
    {
        var found = false;
        ReadJson(jsonPath, root => found = root.TryGetProperty("mcpServers", out var map) && map.ValueKind == JsonValueKind.Object);
        return found;
    }

    /// <summary>"npx -y @scope/server --port 3000"; values that look like keys or tokens are hidden.</summary>
    public static string Describe(string command, IEnumerable<string> arguments)
    {
        var parts = new List<string> { command };
        var hideNext = false;
        foreach (var argument in arguments)
        {
            if (hideNext)
            {
                parts.Add(Masked);
                hideNext = false;
                continue;
            }

            var equals = argument.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && LooksSecretName(argument[..equals]))
            {
                parts.Add($"{argument[..(equals + 1)]}{Masked}");
            }
            else if (argument.StartsWith('-') && LooksSecretName(argument))
            {
                parts.Add(argument);
                hideNext = true;
            }
            else
            {
                parts.Add(LooksLikeToken(argument) ? Masked : argument);
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Codex's config.toml: [mcp_servers.NAME] tables with command / args / url. Sub-tables such as
    /// [mcp_servers.NAME.env] belong to the same server; any other table ends it.
    /// </summary>
    public static IReadOnlyList<McpServer> ReadCodex(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var servers = new List<McpServer>();
        string? name = null;
        string? command = null;
        string? url = null;
        var arguments = new List<string>();
        var inServerTable = false;

        void Flush()
        {
            if (name is not null && (command is not null || url is not null))
            {
                servers.Add(new McpServer(name, "Codex", command is not null ? Describe(command, arguments) : MaskUrl(url!), path));
            }

            (name, command, url, inServerTable) = (null, null, null, false);
            arguments.Clear();
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.StartsWith('['))
            {
                var header = line.Trim('[', ']').Trim();
                if (header.StartsWith("mcp_servers.", StringComparison.Ordinal))
                {
                    var serverName = TomlKeyHead(header["mcp_servers.".Length..], out var rest);
                    if (rest.Length > 0 && serverName == name)
                    {
                        inServerTable = false; // e.g. [mcp_servers.x.env]: keys are environment variables
                        continue;
                    }

                    Flush();
                    name = serverName;
                    inServerTable = true;
                }
                else
                {
                    Flush();
                }

                continue;
            }

            if (!inServerTable || line.Length == 0 || line.StartsWith('#') || !line.Contains('=', StringComparison.Ordinal))
            {
                continue;
            }

            var key = line[..line.IndexOf('=', StringComparison.Ordinal)].Trim();
            var value = line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..].Trim();
            if (key == "args" && value.StartsWith('['))
            {
                // Arrays may span several lines.
                var array = new StringBuilder(value);
                while (!array.ToString().TrimEnd().EndsWith(']') && index + 1 < lines.Length)
                {
                    array.Append(' ').Append(lines[++index].Trim());
                }

                arguments.AddRange(TomlStrings(array.ToString()));
            }
            else if (key is "command" or "url")
            {
                var text = TomlStrings(value).FirstOrDefault();
                if (key == "command")
                {
                    command = text;
                }
                else
                {
                    url = text;
                }
            }
        }

        Flush();
        return servers;
    }

    private static void AddFile(List<McpServer> servers, string path, string property, string client) =>
        ReadJson(path, root => AddServers(servers, root, property, client, path));

    private static void AddServers(List<McpServer> servers, JsonElement container, string property, string client, string configPath)
    {
        if (!container.TryGetProperty(property, out var map) || map.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var server in map.EnumerateObject().Where(server => server.Value.ValueKind == JsonValueKind.Object))
        {
            var command = Text(server.Value, "command");
            var url = Text(server.Value, "url") ?? Text(server.Value, "serverUrl") ?? Text(server.Value, "httpUrl");
            var arguments = server.Value.TryGetProperty("args", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(argument => argument.ValueKind == JsonValueKind.String ? argument.GetString() : argument.GetRawText()).OfType<string>()
                : [];
            var line = command is not null ? Describe(command, arguments) : url is not null ? MaskUrl(url) : null;
            if (line is not null)
            {
                servers.Add(new McpServer(server.Name, client, line, configPath));
            }
        }
    }

    private static void ReadJson(string path, Action<JsonElement> read)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024)
            {
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path), JsonOptions);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                read(document.RootElement);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A broken or locked config just contributes nothing.
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static bool LooksSecretName(string name)
    {
        var lower = name.ToLowerInvariant();
        return SecretWords.Any(word => lower.Contains(word, StringComparison.Ordinal));
    }

    /// <summary>Long random-looking values (API keys, tokens) — not paths, URLs or package names.</summary>
    private static bool LooksLikeToken(string value) =>
        value.Length >= 24 && TokenPattern().IsMatch(value) && value.Any(char.IsDigit) && value.Any(char.IsLetter);

    private static string MaskUrl(string url) => SecretQueryPattern().Replace(url, match => $"{match.Groups[1].Value}={Masked}");

    /// <summary>The first dotted key part ("node_repl" of "node_repl.env"), quotes removed.</summary>
    private static string TomlKeyHead(string key, out string rest)
    {
        key = key.Trim();
        if (key.StartsWith('"') || key.StartsWith('\''))
        {
            var quote = key[0];
            var end = key.IndexOf(quote, 1);
            if (end > 0)
            {
                rest = key[(end + 1)..].TrimStart('.');
                return key[1..end];
            }
        }

        var dot = key.IndexOf('.', StringComparison.Ordinal);
        rest = dot < 0 ? string.Empty : key[(dot + 1)..];
        return dot < 0 ? key : key[..dot];
    }

    /// <summary>The string values of a TOML string or array: "basic" (with escapes) and 'literal'.</summary>
    private static IEnumerable<string> TomlStrings(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var quote = value[index];
            if (quote is not ('"' or '\''))
            {
                continue;
            }

            var text = new StringBuilder();
            for (index++; index < value.Length && value[index] != quote; index++)
            {
                if (quote == '"' && value[index] == '\\' && index + 1 < value.Length)
                {
                    index++;
                    text.Append(value[index] switch { 'n' => '\n', 't' => '\t', _ => value[index] });
                }
                else
                {
                    text.Append(value[index]);
                }
            }

            yield return text.ToString();
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.]+$")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"(?i)([?&](?:[a-z_]*(?:key|token|secret|password|auth)[a-z_]*))=[^&#]+")]
    private static partial Regex SecretQueryPattern();
}

using System.IO.Compression;
using Nexus.Core.Ai;

namespace Nexus.Core.Tests;

public sealed class AiTests : IDisposable
{
    private readonly TempFolder _temp = new();

    [Fact]
    public void SkillFrontMatter_ReadsFoldedDescriptionsAndQuotedNames()
    {
        var (name, description) = SkillLibrary.ParseFrontMatter(
        [
            "---",
            "name: \"pdf-tools\"",
            "description: >",
            "  Fill, merge and split PDF files.",
            "  Use when the user mentions a PDF.",
            "---",
            "# PDF tools"
        ]);

        Assert.Equal("pdf-tools", name);
        Assert.Equal("Fill, merge and split PDF files. Use when the user mentions a PDF.", description);
    }

    [Fact]
    public void SkillLibrary_ReadsUserPluginAndCodexSkills_NewestPluginVersionOnly()
    {
        var profile = _temp.Path;
        Skill(@".claude\skills\notes", "notes", "Take notes");
        Skill(@".claude\plugins\cache\market\audit\1.5.5\skills\scan", "scan-old", "Old");
        Skill(@".claude\plugins\cache\market\audit\1.15.0\skills\scan", "scan", "New");
        Skill(@".codex\skills\.system\imagegen", "imagegen", "Images");

        var skills = SkillLibrary.Load(profile);

        Assert.Equal(["imagegen", "notes", "scan"], skills.Select(skill => skill.Name));
        Assert.Equal("Плагин «audit»", skills.Single(skill => skill.Name == "scan").Source);
        Assert.Equal("Codex (встроенный)", skills.Single(skill => skill.Name == "imagegen").Source);
    }

    [Fact]
    public void McpConfigs_ReadsClaudeCodeClaudeDesktopAndCodexWithoutSecrets()
    {
        var profile = _temp.Folder("profile");
        var appData = _temp.Folder("appdata");
        _temp.File(@"profile\.claude.json", """
            {
              "mcpServers": { "files": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\Work"] } },
              "projects": { "C:/Work/app": { "mcpServers": { "github": { "command": "github-mcp", "args": ["--token", "ghp_123456789012345678901234567890"] } } } }
            }
            """);
        _temp.File(@"appdata\Claude\claude_desktop_config.json", """
            { "mcpServers": { "search": { "url": "https://mcp.example.com/sse?api_key=abc123" } } }
            """);
        _temp.File(@"profile\.codex\config.toml", """
            [mcp_servers.node_repl]
            command = "node"
            args = [
              "C:\\tools\\repl.js",
              "--port=3000",
            ]

            [mcp_servers.node_repl.env]
            API_KEY = "secret-value"

            [mcp_servers.blender]
            command = 'blender-mcp'

            [projects.'c:\work']
            trust_level = "trusted"
            """);

        var servers = McpConfigs.Load(profile, appData);

        Assert.Contains(servers, server => server is { Name: "files", Client: "Claude Code" } && server.Command.Contains("server-filesystem"));
        var github = Assert.Single(servers, server => server.Name == "github");
        Assert.Equal("github-mcp --token ••••", github.Command);
        Assert.Equal("https://mcp.example.com/sse?api_key=••••", Assert.Single(servers, server => server.Name == "search").Command);
        Assert.Equal(@"node C:\tools\repl.js --port=3000", Assert.Single(servers, server => server.Name == "node_repl").Command);
        Assert.Equal("Codex", Assert.Single(servers, server => server.Name == "blender").Client);
        Assert.DoesNotContain(servers, server => server.Command.Contains("secret-value"));
    }

    [Fact]
    public void McpDescribe_HidesTokensButKeepsPathsAndPackages()
    {
        Assert.Equal(
            "npx -y @scope/server --api-key=•••• •••• C:\\Users\\me\\data",
            McpConfigs.Describe("npx", ["-y", "@scope/server", "--api-key=sk-live", "sk2abcdefghijklmnopqrstuvwxyz0123", @"C:\Users\me\data"]));
    }

    [Fact]
    public void LocalModels_ReadsOllamaManifestsAndLmStudioFiles()
    {
        var ollama = _temp.Folder("ollama");
        _temp.File(@"ollama\manifests\registry.ollama.ai\library\llama3\8b", """
            { "config": { "digest": "sha256:abc", "size": 485 }, "layers": [ { "size": 4661211424 }, { "size": 12403 } ] }
            """);
        _temp.File(@"ollama\blobs\sha256-abc", """{ "model_type": "8.0B", "file_type": "Q4_0" }""");
        _temp.File(@"lmstudio\lmstudio-community\Qwen2.5-7B-GGUF\Qwen2.5-7B-Instruct-Q4_K_M.gguf");
        _temp.File(@"lmstudio\lmstudio-community\Qwen2.5-7B-GGUF\mmproj-model-f16.gguf");

        var model = Assert.Single(LocalModels.LoadOllama(ollama));
        Assert.Equal("llama3:8b", model.Name);
        Assert.Equal(4661223827, model.SizeBytes);
        Assert.Equal(("8.0B", "Q4_0"), (model.Parameters, model.Quantization));

        var gguf = Assert.Single(LocalModels.LoadLmStudio(Path.Combine(_temp.Path, "lmstudio")));
        Assert.Equal(("7B", "Q4_K_M"), (gguf.Parameters, gguf.Quantization));
    }

    [Fact]
    public void AiProjectScanner_FindsAgentAndAiProjectsAndAgentHistory()
    {
        var profile = _temp.Path;
        _temp.File(@"Desktop\bot\CLAUDE.md", "# bot");
        _temp.File(@"Documents\vision\requirements.txt", "torch==2.4\nultralytics");
        _temp.File(@"Desktop\plain\notes.txt", "nothing");
        var history = _temp.Folder(@"Desktop\coursework");
        var scratch = _temp.Folder(@"AppData\Roaming\Claude\scratch\x");
        _temp.File(".claude.json", $$"""
            { "projects": { "{{history.Replace('\\', '/')}}": {}, "{{scratch.Replace('\\', '/')}}": {} } }
            """);

        var projects = AiProjectScanner.Load(profile, [], TestContext.Current.CancellationToken);

        Assert.Equal(["bot", "coursework", "vision"], projects.Select(project => project.Name).Order());
        Assert.Equal(AiProjectScanner.AgentProjectKind, projects.Single(project => project.Name == "bot").Kind);
        Assert.Equal(AiProjectScanner.AiCodeKind, projects.Single(project => project.Name == "vision").Kind);
        Assert.Contains("Claude Code", projects.Single(project => project.Name == "coursework").Signals);
    }

    [Fact]
    public void AiProjectScanner_SkipsProfileDriveRootsAndWindowsAndRestoresCodexCasing()
    {
        var profile = _temp.Path;
        var chat = _temp.Folder(@"Documents\Codex\2026-09-28\My-Chat");
        _temp.File(".claude.json", $$"""
            { "projects": { "{{profile.Replace('\\', '/')}}": {}, "C:/": {}, "{{Environment.SystemDirectory.Replace('\\', '/')}}": {} } }
            """);
        _temp.File(@".codex\config.toml", $"[projects.'{chat.ToLowerInvariant()}']\ntrust_level = \"trusted\"\n");

        var projects = AiProjectScanner.Load(profile, [], TestContext.Current.CancellationToken);

        var project = Assert.Single(projects);
        Assert.Equal(chat, project.Path);
        Assert.Equal("My-Chat", project.Name);
    }

    [Fact]
    public void AiFileFinder_GathersSkillsModelsConfigsAndPromptsFromDownloads()
    {
        var downloads = _temp.Folder("Downloads");
        Skill(@"Downloads\my-skill", "my-skill", "Mine");
        _temp.File(@"Downloads\packed.skill");
        Zip(@"Downloads\archive-skill.zip", ("archive-skill/SKILL.md", "---\nname: archive-skill\n---"));
        Zip(@"Downloads\photos.zip", ("photo.jpg", "jpeg"));
        File.WriteAllBytes(_temp.File(@"Downloads\models\tiny-llm.gguf"), new byte[2 * 1024 * 1024]);
        File.WriteAllBytes(_temp.File(@"Downloads\stub.gguf"), new byte[10]);
        _temp.File(@"Downloads\servers.json", """{ "mcpServers": { "x": { "command": "x" } } }""");
        _temp.File(@"Downloads\data.json", """{ "items": [] }""");
        _temp.File(@"Downloads\review.prompt.md", "Review this");
        Skill(@"Downloads\repo\inner-skill", "inner", "In a repository");
        _temp.Folder(@"Downloads\repo\.git");
        Skill(@"Downloads\app\node_modules\pkg\skill", "pkg", "Dependency");

        var files = AiFileFinder.Find([downloads], new HashSet<string>(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            ["archive-skill.zip", "my-skill", "packed.skill", "review.prompt.md", "servers.json", "tiny-llm.gguf"],
            files.Select(file => file.IsFolder ? file.Name : Path.GetFileName(file.Path)).Order());
        Assert.True(files.Single(file => file.Name == "my-skill").IsFolder);
        Assert.Equal(AiFileKind.McpConfig, files.Single(file => file.Path.EndsWith("servers.json")).Kind);
    }

    [Fact]
    public void SkillInstaller_ExtractsArchivesWithATopFolder()
    {
        var archive = Zip("download.zip", ("pdf-skill/SKILL.md", "---\nname: pdf\n---"), ("pdf-skill/scripts/run.py", "print()"));

        var source = SkillInstaller.PrepareSource(archive, _temp.Folder("work"));

        Assert.True(File.Exists(Path.Combine(source, "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(source, "scripts", "run.py")));
        Assert.Equal("pdf-skill", Path.GetFileName(source));
    }

    public void Dispose() => _temp.Dispose();

    private void Skill(string relativeFolder, string name, string description) =>
        _temp.File(Path.Combine(relativeFolder, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\n");

    private string Zip(string relativePath, params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(_temp.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }

        return path;
    }
}

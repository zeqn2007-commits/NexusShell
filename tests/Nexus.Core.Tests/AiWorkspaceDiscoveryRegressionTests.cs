using Nexus.Core.Models;
using Nexus.Core.Services;

namespace Nexus.Core.Tests;

internal static class AiWorkspaceDiscoveryRegressionTests
{
    public static async Task RunAsync(string testRoot)
    {
        var root = Directory.CreateDirectory(
            Path.Combine(testRoot, "AiDiscoveryRegression")).FullName;

        var dotnetProject = CreateDirectory(root, "Dotnet Copilot");
        await File.WriteAllTextAsync(
            Path.Combine(dotnetProject, "DotnetCopilot.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.Extensions.AI" Version="9.0.0" />
              </ItemGroup>
            </Project>
            """);

        var nestedSourceProject = CreateDirectory(root, "Vision Runtime");
        Directory.CreateDirectory(Path.Combine(nestedSourceProject, ".git"));
        var nestedSource = CreateDirectory(
            nestedSourceProject,
            "src",
            "runtime");
        await File.WriteAllTextAsync(
            Path.Combine(nestedSource, "client.py"),
            "from google import genai\nclient = genai.Client()");

        var claudeProject = CreateDirectory(root, "Research Console");
        var claudeRules = CreateDirectory(claudeProject, ".claude");
        await File.WriteAllTextAsync(
            Path.Combine(claudeRules, "settings.json"),
            "{}");

        var cursorProject = CreateDirectory(root, "Desktop Client");
        var cursorRules = CreateDirectory(cursorProject, ".cursor", "rules");
        await File.WriteAllTextAsync(
            Path.Combine(cursorRules, "project.mdc"),
            "Always run tests.");

        var ollamaWorkspace = CreateDirectory(root, ".ollama");
        CreateDirectory(ollamaWorkspace, "models", "manifests");
        var lmStudioWorkspace = CreateDirectory(root, ".lmstudio");
        CreateDirectory(lmStudioWorkspace, "models");
        var comfyUi = CreateDirectory(root, "ComfyUI");
        CreateDirectory(comfyUi, "custom_nodes");
        var stableDiffusion = CreateDirectory(
            root,
            "stable-diffusion-webui-forge");
        CreateDirectory(stableDiffusion, "extensions");
        var llamaCpp = CreateDirectory(root, "llama.cpp");
        await File.WriteAllTextAsync(
            Path.Combine(llamaCpp, "CMakeLists.txt"),
            "cmake_minimum_required(VERSION 3.14)");

        var hiddenProject = CreateDirectory(root, ".hidden-ai-worker");
        await File.WriteAllTextAsync(
            Path.Combine(hiddenProject, "pyproject.toml"),
            "dependencies = [\"pydantic-ai\"]");

        var monorepo = CreateDirectory(root, "Product Monorepo");
        Directory.CreateDirectory(Path.Combine(monorepo, ".git"));
        await File.WriteAllTextAsync(
            Path.Combine(monorepo, "package.json"),
            "{ \"private\": true }");
        var aiPackage = CreateDirectory(monorepo, "apps", "assistant");
        await File.WriteAllTextAsync(
            Path.Combine(aiPackage, "package.json"),
            "{ \"dependencies\": { \"@anthropic-ai/sdk\": \"latest\" } }");
        var ordinaryPackage = CreateDirectory(monorepo, "apps", "accounting");
        await File.WriteAllTextAsync(
            Path.Combine(ordinaryPackage, "package.json"),
            "{ \"dependencies\": { \"sqlite\": \"latest\" } }");

        var nestedModelProject = CreateDirectory(root, "Weights Workspace");
        await File.WriteAllTextAsync(
            Path.Combine(nestedModelProject, "README.md"),
            "# Weights");
        var nestedModels = CreateDirectory(
            nestedModelProject,
            "models",
            "vendor");
        await File.WriteAllBytesAsync(
            Path.Combine(nestedModels, "vision.safetensors"),
            new byte[128 * 1024]);

        var largeLockProject = CreateDirectory(root, "Provider Client");
        var largeLock = string.Concat(
            "{\"padding\":\"",
            new string('x', 300 * 1024),
            "\",\"dependencies\":{\"openai\":\"latest\"}}");
        await File.WriteAllTextAsync(
            Path.Combine(largeLockProject, "package-lock.json"),
            largeLock);

        var ordinaryModels = CreateDirectory(root, "Domain Models");
        Directory.CreateDirectory(Path.Combine(ordinaryModels, ".git"));
        Directory.CreateDirectory(Path.Combine(ordinaryModels, "models"));
        await File.WriteAllTextAsync(
            Path.Combine(ordinaryModels, "package.json"),
            "{ \"dependencies\": { \"sqlite\": \"latest\" } }");
        await File.WriteAllTextAsync(
            Path.Combine(ordinaryModels, "README.md"),
            "# Ordinary domain model library");

        var ordinaryAgent = CreateDirectory(root, "Build Agent");
        await File.WriteAllTextAsync(
            Path.Combine(ordinaryAgent, "package.json"),
            "{ \"dependencies\": { \"execa\": \"latest\" } }");
        await File.WriteAllTextAsync(
            Path.Combine(ordinaryAgent, "README.md"),
            "# Build automation");

        var service = new AiWorkspaceService(
            searchRoots: [root, root + Path.DirectorySeparatorChar],
            includeDefaultRoots: false,
            maximumDepth: 7,
            maximumVisitedDirectories: 2_000,
            maximumResults: 100);
        var projects = await service.DiscoverProjectsAsync();

        CheckFound(projects, dotnetProject, AiProjectKind.Project);
        CheckFound(projects, nestedSourceProject, AiProjectKind.Project);
        CheckNotFound(projects, nestedSource);
        CheckFound(projects, claudeProject, AiProjectKind.AgentWorkspace);
        CheckFound(projects, cursorProject, AiProjectKind.AgentWorkspace);
        CheckFound(projects, ollamaWorkspace, AiProjectKind.ModelWorkspace);
        CheckFound(projects, lmStudioWorkspace, AiProjectKind.ModelWorkspace);
        CheckFound(projects, comfyUi, AiProjectKind.AiTool);
        CheckFound(projects, stableDiffusion, AiProjectKind.AiTool);
        CheckFound(projects, llamaCpp, AiProjectKind.AiTool);
        CheckFound(projects, hiddenProject, AiProjectKind.Project);
        CheckFound(projects, aiPackage, AiProjectKind.Project);
        CheckNotFound(projects, ordinaryPackage);
        CheckFound(
            projects,
            nestedModelProject,
            AiProjectKind.ModelWorkspace);
        CheckFound(projects, largeLockProject, AiProjectKind.Project);
        CheckNotFound(projects, ordinaryModels);
        CheckNotFound(projects, ordinaryAgent);

        Check(
            service.GetSearchRoots().Count == 1,
            "AI-discovery должен дедуплицировать эквивалентные корни.");
        var diagnostics = service.GetLastDiscoveryDiagnostics();
        Check(
            diagnostics is not null
            && !diagnostics.DirectoryLimitReached
            && !diagnostics.ResultLimitReached
            && !diagnostics.DepthLimitReached
            && diagnostics.ResultCount == projects.Count,
            "Полный AI-поиск должен публиковать непротиворечивую диагностику.");

        await VerifyResultLimitAsync(testRoot);
        await VerifyDepthAndDirectoryLimitsAsync(testRoot);
        await VerifyConfiguredOllamaRootAsync(testRoot);
    }

    private static async Task VerifyResultLimitAsync(string testRoot)
    {
        var root = CreateDirectory(testRoot, "AiResultLimit");
        for (var index = 0; index < 12; index++)
        {
            var project = CreateDirectory(root, $"AI-project-{index:D2}");
            await File.WriteAllTextAsync(
                Path.Combine(project, "package.json"),
                "{ \"dependencies\": { \"openai\": \"latest\" } }");
        }

        var service = new AiWorkspaceService(
            searchRoots: [root],
            includeDefaultRoots: false,
            maximumDepth: 2,
            maximumVisitedDirectories: 500,
            maximumResults: 10);
        var projects = await service.DiscoverProjectsAsync();
        var diagnostics = service.GetLastDiscoveryDiagnostics();
        Check(
            projects.Count == 10
            && diagnostics is not null
            && diagnostics.ResultLimitReached
            && diagnostics.IsTruncated,
            "Лимит AI-результатов должен применяться после дедупликации и быть видимым в диагностике.");
    }

    private static async Task VerifyDepthAndDirectoryLimitsAsync(
        string testRoot)
    {
        var depthRoot = CreateDirectory(testRoot, "AiDepthLimit");
        var levelOne = CreateDirectory(depthRoot, "level-one");
        var levelTwo = CreateDirectory(levelOne, "level-two");
        await File.WriteAllTextAsync(
            Path.Combine(levelTwo, "requirements.txt"),
            "openai==2.0.0");
        var depthService = new AiWorkspaceService(
            searchRoots: [depthRoot],
            includeDefaultRoots: false,
            maximumDepth: 1,
            maximumVisitedDirectories: 500,
            maximumResults: 50);
        var depthProjects = await depthService.DiscoverProjectsAsync();
        Check(
            depthProjects.Count == 0
            && depthService.GetLastDiscoveryDiagnostics()?.DepthLimitReached == true,
            "Обрезание AI-поиска по глубине должно быть явно диагностировано.");

        var directoryRoot = CreateDirectory(testRoot, "AiDirectoryLimit");
        for (var index = 0; index < 120; index++)
        {
            CreateDirectory(directoryRoot, $"folder-{index:D3}");
        }

        var directoryService = new AiWorkspaceService(
            searchRoots: [directoryRoot],
            includeDefaultRoots: false,
            maximumDepth: 2,
            maximumVisitedDirectories: 100,
            maximumResults: 50);
        await directoryService.DiscoverProjectsAsync();
        Check(
            directoryService.GetLastDiscoveryDiagnostics()
                ?.DirectoryLimitReached == true,
            "Обрезание AI-поиска по числу папок должно быть явно диагностировано.");
    }

    private static async Task VerifyConfiguredOllamaRootAsync(
        string testRoot)
    {
        var configuredRoot = CreateDirectory(
            testRoot,
            "CustomModelStorage");
        CreateDirectory(configuredRoot, "blobs");
        var previousValue = Environment.GetEnvironmentVariable(
            "OLLAMA_MODELS");
        try
        {
            Environment.SetEnvironmentVariable(
                "OLLAMA_MODELS",
                configuredRoot);
            var service = new AiWorkspaceService(
                searchRoots: [configuredRoot],
                includeDefaultRoots: false,
                maximumDepth: 2,
                maximumVisitedDirectories: 500,
                maximumResults: 50);
            var projects = await service.DiscoverProjectsAsync();
            CheckFound(
                projects,
                configuredRoot,
                AiProjectKind.ModelWorkspace);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "OLLAMA_MODELS",
                previousValue);
        }
    }

    private static string CreateDirectory(
        string root,
        params string[] segments)
    {
        var path = segments.Aggregate(root, Path.Combine);
        return Directory.CreateDirectory(path).FullName;
    }

    private static void CheckFound(
        IReadOnlyList<AiProjectEntry> projects,
        string path,
        AiProjectKind kind)
    {
        var matches = projects.Where(project => PathsEqual(
            project.FullPath,
            path)).ToArray();
        Check(
            matches.Length == 1 && matches[0].Kind == kind,
            $"AI-проект «{path}» должен быть найден один раз как {kind}; "
            + $"совпадений: {matches.Length}, виды: "
            + string.Join(", ", matches.Select(match => match.Kind))
            + $". Найдено: {string.Join(" | ", projects.Select(project => $"{project.Kind}:{project.FullPath}"))}");
    }

    private static void CheckNotFound(
        IReadOnlyList<AiProjectEntry> projects,
        string path) =>
        Check(
            projects.All(project => !PathsEqual(project.FullPath, path)),
            $"Обычная или вложенная служебная папка «{path}» не должна быть отдельным AI-проектом.");

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

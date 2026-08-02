using Nexus.Core.Models;
using Nexus.Core.Services;

namespace Nexus.Core.Tests;

internal static class CatalogRegressionTests
{
    public static async Task RunAsync(string testRoot)
    {
        var catalogRoot = Path.Combine(testRoot, "CatalogRegression");
        Directory.CreateDirectory(catalogRoot);
        var iconPath = Path.Combine(catalogRoot, "catalog-icon.png");
        await File.WriteAllBytesAsync(iconPath, [1]);

        await KeepsSameNameApplicationsWithDifferentIdsAsync(
            catalogRoot,
            iconPath);
        await ClassifiesPackagedAndSystemApplicationsAsync(
            catalogRoot,
            iconPath);
        await KeepsUnlockersOutOfGamesButInsideApplicationsAsync(
            catalogRoot,
            iconPath);
    }

    private static async Task KeepsSameNameApplicationsWithDifferentIdsAsync(
        string catalogRoot,
        string iconPath)
    {
        StartApplicationInfo[] source =
        [
            new(
                "Общее имя",
                @"shell:AppsFolder\Contoso.First_abcd!App",
                "Contoso.First_abcd!App",
                IconSourcePath: iconPath),
            new(
                "Общее имя",
                @"shell:AppsFolder\Contoso.Second_abcd!App",
                "Contoso.Second_abcd!App",
                IconSourcePath: iconPath)
        ];
        var service = new FileSystemService(
            Path.Combine(catalogRoot, "same-name-view.json"),
            startMenuRoots: [],
            startApplications: source);

        var applications = await service.GetInstalledApplicationsAsync();

        Assert(
            source.Select(StartApplicationCatalog.GetStableIdentity)
                .ToHashSet(StringComparer.OrdinalIgnoreCase).Count == 2
            && applications.Count == 2
            && applications.All(application =>
                application.Name == "Общее имя")
            && applications.Select(application => application.FullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase).Count == 2,
            "Каталог приложений не должен склеивать разные AppID с одинаковым отображаемым именем.");
    }

    private static async Task ClassifiesPackagedAndSystemApplicationsAsync(
        string catalogRoot,
        string iconPath)
    {
        var parsed = StartApplicationCatalog.ParseStartAppsJson(
            """
            [
              {
                "Name": "Contoso Reader",
                "AppID": "Contoso.Reader_abcd1234!App"
              },
              {
                "Name": "Параметры",
                "AppID": "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel"
              }
            ]
            """)
            .Select(application => application with
            {
                IconSourcePath = iconPath
            })
            .ToArray();
        var service = new FileSystemService(
            Path.Combine(catalogRoot, "packaged-view.json"),
            startMenuRoots: [],
            startApplications: parsed);

        var applications = await service.GetInstalledApplicationsAsync();

        Assert(
            applications.Single(application =>
                    application.Name == "Contoso Reader")
                .DisplayType == "Приложение Microsoft Store",
            "Обычный packaged AppID должен отображаться как приложение Microsoft Store.");
        Assert(
            applications.Single(application =>
                    application.Name == "Параметры")
                .DisplayType == "Системное приложение",
            "Известный системный AppID должен отображаться как системное приложение.");
    }

    private static async Task KeepsUnlockersOutOfGamesButInsideApplicationsAsync(
        string catalogRoot,
        string iconPath)
    {
        var applicationsRoot = Path.Combine(catalogRoot, "UnlockerApplications");
        Directory.CreateDirectory(applicationsRoot);
        var startApplications = new List<StartApplicationInfo>();
        foreach (var name in new[] { "SimpleUnlocker", "Unlocker" })
        {
            var installPath = Path.Combine(applicationsRoot, name);
            Directory.CreateDirectory(installPath);
            var executablePath = Path.Combine(installPath, $"{name}.exe");
            var shortcutPath = Path.Combine(catalogRoot, $"{name}.lnk");
            await File.WriteAllBytesAsync(executablePath, new byte[96 * 1024]);
            await File.WriteAllBytesAsync(
                Path.Combine(installPath, "UnityPlayer.dll"),
                [1]);
            await File.WriteAllBytesAsync(shortcutPath, [1]);
            startApplications.Add(new StartApplicationInfo(
                name,
                shortcutPath,
                IconSourcePath: iconPath,
                ResolvedExecutablePath: executablePath,
                WorkingDirectory: installPath,
                SourcePath: shortcutPath));
        }

        var gameLibrary = new GameLibraryService(
            steamRoots: [],
            epicManifestDirectory: Path.Combine(
                catalogRoot,
                "MissingEpicManifests"),
            xboxRoots: [],
            localGameRoots: [applicationsRoot],
            localGameRootsPath: Path.Combine(
                catalogRoot,
                "unlocker-game-folders.json"),
            ignoredGamesPath: Path.Combine(
                catalogRoot,
                "unlocker-ignored-games.json"),
            automaticGameRoots: [],
            startApplications: startApplications);
        var games = await gameLibrary.GetInstalledGamesAsync();

        Assert(
            games.Count == 0,
            "SimpleUnlocker и Unlocker не должны попадать в библиотеку локальных игр даже рядом с игровым DLL.");

        var applicationCatalog = new FileSystemService(
            Path.Combine(catalogRoot, "unlocker-applications-view.json"),
            startMenuRoots: [],
            startApplications: startApplications);
        var applications =
            await applicationCatalog.GetInstalledApplicationsAsync();

        Assert(
            new[] { "SimpleUnlocker", "Unlocker" }.All(name =>
                applications.Any(application =>
                    application.Name.Equals(
                        name,
                        StringComparison.OrdinalIgnoreCase)
                    && application.DisplayType == "Приложение")),
            "Unlocker-подобная утилита должна оставаться в каталоге приложений, если у неё есть корректная Start-запись.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

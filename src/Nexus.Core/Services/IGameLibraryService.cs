using Nexus.Core.Models;

namespace Nexus.Core.Services;

public interface IGameLibraryService
{
    Task<IReadOnlyList<GameEntry>> GetInstalledGamesAsync(
        CancellationToken cancellationToken = default);

    Task<bool> AddLocalGameFolderAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveLocalGameFolderAsync(
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Возвращает только папки, явно добавленные пользователем.
    /// Недоступные сейчас пути сохраняются, чтобы их можно было убрать.
    /// </summary>
    IReadOnlyList<string> GetLocalGameFolders();

    /// <summary>
    /// Возвращает автоматически выбранные корни только для чтения.
    /// Они не являются пользовательской настройкой и не удаляются из JSON.
    /// </summary>
    IReadOnlyList<string> GetAutomaticGameFolders();

    IReadOnlyList<string> GetScanRoots();

    int HiddenGamesCount { get; }

    Task<bool> HideGameAsync(
        GameEntry game,
        CancellationToken cancellationToken = default);

    Task<int> RestoreHiddenGamesAsync(
        CancellationToken cancellationToken = default);

    void Launch(GameEntry game);

    void OpenInstallFolder(GameEntry game);
}

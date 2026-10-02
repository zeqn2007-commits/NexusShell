using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Nexus.App.Models;
using Nexus.App.Shell;
using Nexus.Core.Games;

namespace Nexus.App.ViewModels;

/// <summary>"Игры": installed games from Steam, Epic Games, Xbox, GOG and the user's own folders.</summary>
public sealed partial class GamesViewModel(GameLibrary library, ShellViewModel shell) : ObservableObject
{
    private IReadOnlyList<GameItem> _all = [];
    private GameSource? _filter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial IReadOnlyList<GameItem> Games { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial GameItem? SelectedGame { get; set; }

    /// <summary>"Продолжить игру": the most recently played game.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeatured), nameof(FeaturedDetails))]
    public partial GameItem? Featured { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>The sources that have games, for the filter tabs.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GameSource> Sources { get; private set; } = [];

    public bool HasSelection => SelectedGame is not null;

    public bool HasFeatured => Featured is not null;

    public string FeaturedDetails => Featured is { } game
        ? $"{game.SourceLabel} · последний запуск: {game.DisplayLastPlayed.ToLowerInvariant()}"
        : string.Empty;

    public string CountText => Formatting.Count(Games.Count, "игра", "игры", "игр");

    public string TotalSizeText => Formatting.Size(_all.Sum(game => game.SizeBytes ?? 0));

    public int HiddenCount => library.HiddenCount;

    public async Task LoadAsync()
    {
        var selectedId = SelectedGame?.Entry?.Id;
        IsLoading = true;
        try
        {
            _all = (await library.LoadAsync()).Select(GameItem.FromEntry).ToArray();
        }
        finally
        {
            IsLoading = false;
        }

        Featured = _all.Where(game => game.LastPlayed is not null).MaxBy(game => game.LastPlayed);
        Sources = _all.Select(game => game.Source).Distinct().Order().ToArray();
        ApplyFilter(_filter is { } filter && Sources.Contains(filter) ? filter : null);
        SelectedGame = Games.FirstOrDefault(game => game.Entry?.Id == selectedId) ?? Featured ?? Games.FirstOrDefault();
        IsEmpty = _all.Count == 0;
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(TotalSizeText));
    }

    public void ApplyFilter(GameSource? source)
    {
        _filter = source;
        Games = source is null ? _all : _all.Where(game => game.Source == source).ToArray();
    }

    public void Play(GameItem? game)
    {
        if (game?.Entry is not { } entry)
        {
            return;
        }

        try
        {
            GameLibrary.Launch(entry);
            shell.Notify($"«{game.Name}» запускается…");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            shell.NotifyError(exception.Message, "Игра не запустилась");
        }
    }

    public void OpenFolder(GameItem? game)
    {
        if (game is null)
        {
            return;
        }

        if (Directory.Exists(game.InstallPath))
        {
            shell.Navigate(NavLocation.ForFolder(game.InstallPath));
        }
        else
        {
            shell.NotifyError($"Папка «{game.InstallPath}» не найдена. Возможно, игра удалена или диск отключён.", "Папка игры не найдена");
        }
    }

    /// <summary>Hides a game (a tool Steam lists as a game, a demo…); the notification offers to bring it back.</summary>
    public void Hide(GameItem? game)
    {
        if (game?.Entry is not { } entry)
        {
            return;
        }

        library.Hide(entry);
        _all = _all.Where(item => item != game).ToArray();
        if (Featured == game)
        {
            Featured = _all.Where(item => item.LastPlayed is not null).MaxBy(item => item.LastPlayed);
        }

        ApplyFilter(_filter);
        SelectedGame = Featured ?? Games.FirstOrDefault();
        IsEmpty = _all.Count == 0;
        OnPropertyChanged(nameof(HiddenCount));
        shell.Notify($"«{game.Name}» скрыта из библиотеки.", InfoBarSeverity.Success, actionText: "Вернуть", action: async () =>
        {
            library.Unhide(entry);
            await LoadAsync();
        });
    }

    public async Task ShowHiddenAsync()
    {
        library.ShowHidden();
        await LoadAsync();
    }

    public async Task AddFolderAsync(string folder)
    {
        if (!library.AddFolder(folder))
        {
            shell.Notify("Эта папка уже есть в библиотеке.");
            return;
        }

        var before = _all.Count;
        await LoadAsync();
        var found = _all.Count - before;
        shell.Notify(found > 0
            ? $"Найдено новых игр: {found}."
            : "В этой папке не нашлось игр. Добавьте папку, в которой лежат папки игр.",
            found > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
    }
}

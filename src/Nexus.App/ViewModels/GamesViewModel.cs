using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.App.Demo;
using Nexus.App.Models;

namespace Nexus.App.ViewModels;

public sealed partial class GamesViewModel : ObservableObject
{
    private readonly IReadOnlyList<GameItem> _all = DemoData.Games;

    public GamesViewModel()
    {
        Featured = _all
            .Where(game => game.HasHero && game.LastPlayed is not null)
            .OrderByDescending(game => game.LastPlayed)
            .FirstOrDefault();
        ApplyFilter(null);
        SelectedGame = Featured;
    }

    public GameItem? Featured { get; }

    [ObservableProperty]
    public partial IReadOnlyList<GameItem> Games { get; private set; } = [];

    [ObservableProperty]
    public partial GameItem? SelectedGame { get; set; }

    public string CountText => Formatting.Count(Games.Count, "игра", "игры", "игр");

    public string TotalSizeText => Formatting.Size(_all.Sum(game => game.SizeBytes ?? 0));

    public int SteamCount => _all.Count(game => game.Source == GameSource.Steam);

    public void ApplyFilter(GameSource? source)
    {
        Games = source is null ? _all : _all.Where(game => game.Source == source).ToArray();
        OnPropertyChanged(nameof(CountText));
    }
}

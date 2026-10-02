using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nexus.App.Models;
using Nexus.App.Shell;
using Nexus.App.ViewModels;

namespace Nexus.App.Views;

public sealed partial class GamesPage : Page, INotifyPropertyChanged
{
    private readonly ShellViewModel _shell = App.Services.GetRequiredService<ShellViewModel>();

    public GamesPage()
    {
        ViewModel = App.Services.GetRequiredService<GamesViewModel>();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GamesViewModel.SelectedGame))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelection)));
            }
        };
        InitializeComponent();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public GamesViewModel ViewModel { get; }

    public bool HasSelection => ViewModel.SelectedGame is not null;

    public bool HasFeatured => ViewModel.Featured is not null;

    public string FeaturedDetails => ViewModel.Featured is { } game
        ? $"{game.SourceLabel} · последний запуск: {game.DisplayLastPlayed.ToLowerInvariant()}"
        : string.Empty;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _shell.StatusText = ViewModel.CountText;
        _shell.SelectionText = $"Занято на дисках: {ViewModel.TotalSizeText}";
    }

    private void SourceFilter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        GameSource? source = (sender.SelectedItem?.Tag as string) switch
        {
            "steam" => GameSource.Steam,
            "epic" => GameSource.Epic,
            "xbox" => GameSource.Xbox,
            "local" => GameSource.Local,
            _ => null
        };
        ViewModel.ApplyFilter(source);
        _shell.StatusText = ViewModel.CountText;
    }
}

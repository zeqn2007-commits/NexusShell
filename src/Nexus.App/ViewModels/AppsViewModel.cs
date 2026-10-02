using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Nexus.App.Models;
using Nexus.Core.Apps;

namespace Nexus.App.ViewModels;

public sealed partial class AppItemViewModel(StartApp app) : ObservableObject
{
    public StartApp App { get; } = app;

    public string Name => App.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    public partial ImageSource? Icon { get; set; }

    public bool HasIcon => Icon is not null;

    public bool IconRequested { get; set; }

    /// <summary>Screen readers announce items by their string form.</summary>
    public override string ToString() => Name;
}

/// <summary>A letter of the list, as in Start's "All apps".</summary>
public sealed record AppGroup(string Letter, IReadOnlyList<AppItemViewModel> Apps);

/// <summary>"Приложения": everything in Start's "All apps", grouped by letter and filtered by the search box.</summary>
public sealed partial class AppsViewModel : ObservableObject
{
    private IReadOnlyList<AppItemViewModel> _all = [];
    private string _filter = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<AppGroup> Groups { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool NothingFound { get; private set; }

    [ObservableProperty]
    public partial string CountText { get; private set; } = string.Empty;

    public IReadOnlyList<AppItemViewModel> Visible { get; private set; } = [];

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            _all = (await StartApps.LoadAsync()).Select(app => new AppItemViewModel(app)).ToArray();
        }
        finally
        {
            IsLoading = false;
        }

        ApplyFilter(_filter);
    }

    public void ApplyFilter(string text)
    {
        _filter = text.Trim();
        Visible = _filter.Length == 0
            ? _all
            : _all.Where(app => app.Name.Contains(_filter, StringComparison.CurrentCultureIgnoreCase)).ToArray();

        // Groups follow the order of the (naturally sorted) apps: "#", then letters.
        Groups = Visible
            .GroupBy(app => GroupKey(app.Name))
            .OrderBy(group => group.Key == "#" ? 0 : 1)
            .Select(group => new AppGroup(group.Key, group.ToArray()))
            .ToArray();
        NothingFound = !IsLoading && Visible.Count == 0;
        CountText = _filter.Length == 0
            ? Formatting.Count(_all.Count, "приложение", "приложения", "приложений")
            : $"Найдено: {Formatting.Count(Visible.Count, "приложение", "приложения", "приложений")}";
    }

    private static string GroupKey(string name) =>
        name.Length > 0 && char.IsLetter(name[0]) ? char.ToUpper(name[0], System.Globalization.CultureInfo.CurrentCulture).ToString() : "#";
}

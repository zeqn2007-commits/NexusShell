using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace Nexus.App.Shell;

public sealed partial class ShellViewModel : ObservableObject
{
    private Func<Task>? _notificationAction;
    private CancellationTokenSource? _notificationTimeout;

    public ShellViewModel()
    {
        var home = new TabViewModel(NavLocation.Home);
        Tabs.Add(home);
        SelectedTab = home;
    }

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    public partial TabViewModel? SelectedTab { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsNotificationOpen { get; set; }

    [ObservableProperty]
    public partial string NotificationTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NotificationMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity NotificationSeverity { get; set; }

    [ObservableProperty]
    public partial string? NotificationActionText { get; set; }

    public bool HasNotificationAction => NotificationActionText is not null;

    /// <summary>Raised whenever the active tab shows a different location.</summary>
    public event EventHandler<NavLocation>? LocationChanged;

    partial void OnSelectedTabChanged(TabViewModel? oldValue, TabViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnTabPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnTabPropertyChanged;
            LocationChanged?.Invoke(this, newValue.Location);
        }
    }

    partial void OnNotificationActionTextChanged(string? value) => OnPropertyChanged(nameof(HasNotificationAction));

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.Location) && sender is TabViewModel tab && tab == SelectedTab)
        {
            LocationChanged?.Invoke(this, tab.Location);
        }
    }

    public void Navigate(NavLocation location) => SelectedTab?.Navigate(location);

    private string? _pendingSelection;

    /// <summary>Remembers an item to select once its folder has loaded (<c>/select,"file"</c>).</summary>
    public void RequestSelection(string path) => _pendingSelection = path;

    public string? TakePendingSelection(string? folder)
    {
        if (_pendingSelection is { } path && folder is not null
            && Nexus.Core.IO.PathHelper.GetParent(path) is { } parent && Nexus.Core.IO.PathHelper.AreEqual(parent, folder))
        {
            _pendingSelection = null;
            return path;
        }

        return null;
    }

    /// <summary>Shows the bar above the page. Informational messages close themselves.</summary>
    public void Notify(string message, InfoBarSeverity severity = InfoBarSeverity.Informational, string? title = null,
        string? actionText = null, Func<Task>? action = null)
    {
        _notificationTimeout?.Cancel();
        NotificationTitle = title ?? string.Empty;
        NotificationMessage = message;
        NotificationSeverity = severity;
        NotificationActionText = actionText;
        _notificationAction = action;
        IsNotificationOpen = true;

        if (severity is InfoBarSeverity.Informational or InfoBarSeverity.Success)
        {
            var timeout = new CancellationTokenSource();
            _notificationTimeout = timeout;
            _ = CloseLaterAsync(timeout.Token, actionText is null ? 5 : 15);
        }
    }

    public void NotifyError(string message, string title = "Не получилось") =>
        Notify(message, InfoBarSeverity.Error, title);

    [RelayCommand]
    private async Task RunNotificationActionAsync()
    {
        var action = _notificationAction;
        IsNotificationOpen = false;
        _notificationAction = null;
        if (action is not null)
        {
            await action();
        }
    }

    private async Task CloseLaterAsync(CancellationToken token, int seconds)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), token);
            IsNotificationOpen = false;
        }
        catch (TaskCanceledException)
        {
        }
    }

    [RelayCommand]
    public void NewTab()
    {
        var tab = new TabViewModel(NavLocation.Home);
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    public void OpenInNewTab(NavLocation location)
    {
        var tab = new TabViewModel(location);
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    public void CloseTab(TabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        if (Tabs.Count == 1)
        {
            // Closing the last tab keeps the window open on the home page.
            tab.Navigate(NavLocation.Home);
            return;
        }

        Tabs.RemoveAt(index);
        if (SelectedTab == tab)
        {
            SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }
    }

    public void CycleTab(int direction)
    {
        if (SelectedTab is null || Tabs.Count < 2)
        {
            return;
        }

        var index = (Tabs.IndexOf(SelectedTab) + direction + Tabs.Count) % Tabs.Count;
        SelectedTab = Tabs[index];
    }

    [RelayCommand]
    private void GoBack() => SelectedTab?.GoBack();

    [RelayCommand]
    private void GoForward() => SelectedTab?.GoForward();

    [RelayCommand]
    private void GoUp() => SelectedTab?.GoUp();
}

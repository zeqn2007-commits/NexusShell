using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Nexus.App.Services;

/// <summary>Standard Windows 11 content dialogs for confirmations and messages.</summary>
public sealed class DialogService(WindowContext window)
{
    private bool _isOpen;

    public async Task<bool> ConfirmAsync(string title, string message, string primaryText, bool destructive = false)
    {
        var dialog = Create(title, message);
        dialog.PrimaryButtonText = primaryText;
        dialog.CloseButtonText = "Отмена";
        dialog.DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary;
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = Create(title, message);
        dialog.CloseButtonText = "Закрыть";
        dialog.DefaultButton = ContentDialogButton.Close;
        await ShowAsync(dialog);
    }

    private ContentDialog Create(string title, string message) => new()
    {
        Title = title,
        Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 },
        XamlRoot = window.XamlRoot,
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        RequestedTheme = (window.XamlRoot?.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default
    };

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        // Only one ContentDialog may be open at a time in WinUI.
        if (_isOpen || dialog.XamlRoot is null)
        {
            return ContentDialogResult.None;
        }

        _isOpen = true;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _isOpen = false;
        }
    }
}

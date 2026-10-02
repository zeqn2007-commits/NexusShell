using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Nexus.App.Services;
using Nexus.App.Shell;
using Nexus.Core.Integration;
using Nexus.Core.Settings;

namespace Nexus.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private readonly DefaultFileManager _defaultFileManager;
    private readonly DialogService _dialogs;
    private readonly ShellViewModel _shell;
    private bool _updating;

    public SettingsViewModel(SettingsStore settings, DefaultFileManager defaultFileManager, DialogService dialogs, ShellViewModel shell)
    {
        _settings = settings;
        _defaultFileManager = defaultFileManager;
        _dialogs = dialogs;
        _shell = shell;
        _updating = true;
        OpensFolders = SafeIsEnabled();
        ShowHiddenItems = settings.Current.ShowHiddenItems;
        ShowFileExtensions = settings.Current.ShowFileExtensions;
        ThemeIndex = (int)settings.Current.Theme;
        _updating = false;
    }

    [ObservableProperty]
    public partial bool OpensFolders { get; set; }

    [ObservableProperty]
    public partial bool ShowHiddenItems { get; set; }

    [ObservableProperty]
    public partial bool ShowFileExtensions { get; set; }

    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    public string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "—";

    public string DataFolder => SettingsStore.DataDirectory;

    partial void OnShowHiddenItemsChanged(bool value)
    {
        if (!_updating)
        {
            _settings.Update(settings => settings.ShowHiddenItems = value);
        }
    }

    partial void OnShowFileExtensionsChanged(bool value)
    {
        if (!_updating)
        {
            _settings.Update(settings => settings.ShowFileExtensions = value);
        }
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (!_updating && Enum.IsDefined(typeof(ThemePreference), value))
        {
            _settings.Update(settings => settings.Theme = (ThemePreference)value);
        }
    }

    partial void OnOpensFoldersChanged(bool value)
    {
        if (!_updating)
        {
            _ = ApplyDefaultFileManagerAsync(value);
        }
    }

    private async Task ApplyDefaultFileManagerAsync(bool enable)
    {
        try
        {
            if (enable)
            {
                var confirmed = await _dialogs.ConfirmAsync(
                    "Открывать папки в Nexus?",
                    "Папки, диски, Win+E и ярлык Проводника будут открываться в Nexus. Панель задач, «Пуск» и рабочий стол " +
                    "останутся системными. Изменение касается только вашей учётной записи, его можно отменить здесь же в любой момент.",
                    "Открывать в Nexus");
                if (!confirmed || Environment.ProcessPath is not { } executable)
                {
                    SetSilently(false);
                    return;
                }

                _defaultFileManager.Enable(executable);
                _shell.Notify("Теперь папки открываются в Nexus.", InfoBarSeverity.Success);
            }
            else
            {
                _defaultFileManager.Disable();
                _shell.Notify("Папки снова открываются в Проводнике.", InfoBarSeverity.Success);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            SetSilently(SafeIsEnabled());
            _shell.NotifyError(exception.Message, "Не удалось изменить настройку");
        }
    }

    private void SetSilently(bool value)
    {
        _updating = true;
        OpensFolders = value;
        _updating = false;
    }

    private bool SafeIsEnabled()
    {
        try
        {
            return _defaultFileManager.IsEnabled();
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }
}

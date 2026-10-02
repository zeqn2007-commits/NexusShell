using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nexus.App.Shell;
using Nexus.App.ViewModels;

namespace Nexus.App.Views;

public sealed partial class AiCenterPage : Page
{
    private readonly ShellViewModel _shell = App.Services.GetRequiredService<ShellViewModel>();

    public AiCenterPage()
    {
        ViewModel = App.Services.GetRequiredService<AiCenterViewModel>();
        InitializeComponent();
    }

    public AiCenterViewModel ViewModel { get; }

    public string ModelsCaption => $"моделей · {ViewModel.ModelsSize}";

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _shell.StatusText = "Сканирование завершено 2 минуты назад";
        _shell.SelectionText = string.Empty;
    }
}

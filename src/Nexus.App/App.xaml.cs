using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Nexus.App.Services;
using Nexus.App.Shell;
using Nexus.App.ViewModels;
using Nexus.Core.Ai;
using Nexus.Core.Games;
using Nexus.Core.Integration;
using Nexus.Core.Operations;
using Nexus.Core.Settings;
using Nexus.Core.Shell;
using Nexus.Core.Threading;

namespace Nexus.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
        UnhandledException += (_, e) => CrashLog.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                CrashLog.Write(exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            e.SetObserved();
        };
    }

    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var options = StartupOptions.Parse(commandLine);
        var window = new MainWindow(
            Services.GetRequiredService<ShellViewModel>(),
            Services.GetRequiredService<WindowContext>(),
            Services.GetRequiredService<SettingsStore>(),
            options);
        _window = window;
        window.Activate();
        window.HandleLaunch(LaunchRequest.Parse(commandLine), newTab: false);

        // Later launches (a folder double-click, Win+E) arrive here from Program.
        Program.Redirected += (_, line) => window.DispatcherQueue.TryEnqueue(() =>
        {
            window.HandleLaunch(LaunchRequest.Parse(StripExecutable(LaunchRequest.SplitCommandLine(line))), newTab: true);
            window.BringToFront();
        });

        KeepRegistrationValid();
    }

    private static IReadOnlyList<string> StripExecutable(IReadOnlyList<string> parts) =>
        parts.Count > 0 && (parts[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || parts[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            ? parts.Skip(1).ToArray()
            : parts;

    /// <summary>If folders are set to open in Nexus but the registered copy was moved or removed, point them here.</summary>
    private static void KeepRegistrationValid()
    {
        try
        {
            var manager = Services.GetRequiredService<DefaultFileManager>();
            if (manager.IsEnabled() && manager.RegisteredExecutable() is { } registered && !File.Exists(registered)
                && Environment.ProcessPath is { } current)
            {
                manager.Enable(current);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            CrashLog.Write(exception);
        }
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Core
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<FileOperationService>();
        services.AddSingleton<ShellImageProvider>();
        services.AddSingleton(_ => new StaTaskScheduler(2, "Nexus Shell"));
        services.AddSingleton<RecentItems>();
        services.AddSingleton<RecycleBinService>();
        services.AddSingleton<UndoHistory>();
        services.AddSingleton<GameLibrary>();
        services.AddSingleton<AiWorkspace>();
        services.AddSingleton<DefaultFileManager>(_ => new DefaultFileManager());

        // App services
        services.AddSingleton<WindowContext>();
        services.AddSingleton<IconCache>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<PreviewService>();
        services.AddSingleton<ClassicMenuService>();

        // View models
        services.AddSingleton<ShellViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<FolderViewModel>();
        services.AddTransient<ThisPcViewModel>();
        services.AddTransient<RecycleBinViewModel>();
        services.AddTransient<NetworkViewModel>();
        services.AddTransient<AppsViewModel>();
        services.AddTransient<GamesViewModel>();
        services.AddTransient<AiCenterViewModel>();
        services.AddTransient<TorrentsViewModel>();
        services.AddTransient<SettingsViewModel>();
        return services.BuildServiceProvider();
    }
}

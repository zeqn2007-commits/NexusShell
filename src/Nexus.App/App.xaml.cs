using Microsoft.UI.Xaml;

namespace Nexus.App;

public partial class App : Application
{
    private const long MaxLogSizeBytes = 1024 * 1024;
    private Window? _window;

    public App()
    {
        WriteTrace("App constructor: before InitializeComponent");
        InitializeComponent();
        WriteTrace("App constructor: after InitializeComponent");
        RequestedTheme = ApplicationTheme.Dark;
        UnhandledException += App_UnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        WriteTrace("OnLaunched: begin");
        try
        {
            _window = new MainWindow();
            WriteTrace("OnLaunched: MainWindow constructed");
            _window.Activate();
            WriteTrace("OnLaunched: MainWindow activated");
        }
        catch (Exception exception)
        {
            WriteCrashLog(exception);
            throw;
        }
    }

    private static void App_UnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
    }

    private static void WriteCrashLog(Exception exception)
    {
        AppendLog(
            "nexus-crash.log",
            $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
    }

    private static void WriteTrace(string message)
    {
        AppendLog(
            "nexus-startup.log",
            $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
    }

    private static void AppendLog(string fileName, string message)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Nexus Shell",
                "Logs");
            Directory.CreateDirectory(logDirectory);
            var path = Path.Combine(logDirectory, fileName);
            if (File.Exists(path) && new FileInfo(path).Length > MaxLogSizeBytes)
            {
                File.Move(path, path + ".old", overwrite: true);
            }

            File.AppendAllText(path, message);
        }
        catch
        {
            // Диагностика не должна влиять на работу приложения.
        }
    }
}

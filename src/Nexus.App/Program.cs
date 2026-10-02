using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace Nexus.App;

/// <summary>
/// Custom entry point: Nexus runs as a single instance. A second launch (double-clicking
/// a folder, Win+E, "Открыть в Nexus") hands its command line to the running window and exits.
/// </summary>
public static class Program
{
    private const string InstanceKey = "Nexus.Main";

    /// <summary>Raised on a background thread with the command line of a redirected launch.</summary>
    public static event EventHandler<string>? Redirected;

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectToRunningInstance())
        {
            return 0;
        }

        Application.Start(callback =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    private static bool RedirectToRunningInstance()
    {
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (main.IsCurrent)
        {
            main.Activated += (_, activation) => Redirected?.Invoke(null, ReadCommandLine(activation));
            return false;
        }

        // Let the running window come to the front, then forward our activation to it.
        AllowSetForegroundWindow(main.ProcessId);
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        using var done = new ManualResetEventSlim();
        _ = Task.Run(async () =>
        {
            try
            {
                await main.RedirectActivationToAsync(activation);
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait(TimeSpan.FromSeconds(10));
        return true;
    }

    private static string ReadCommandLine(AppActivationArguments activation) =>
        activation.Kind == ExtendedActivationKind.Launch && activation.Data is ILaunchActivatedEventArgs launch
            ? launch.Arguments ?? string.Empty
            : string.Empty;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}

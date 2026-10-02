using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Nexus.App.Shell;
using Nexus.Core.Shell;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;

namespace Nexus.App.Services;

/// <summary>
/// Shows the classic Windows context menu ("Показать дополнительные параметры") from any page,
/// in the page's light or dark theme. Holding Shift adds the extended verbs, as in Explorer.
/// </summary>
public sealed class ClassicMenuService(WindowContext window, ShellViewModel shell)
{
    public static bool IsShiftDown =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

    /// <param name="tryHandleVerb">Sees the chosen verb first; returns true when Nexus carried it out itself.</param>
    public void ShowForItems(FrameworkElement page, IReadOnlyList<string> paths, PointInt32 point, Func<string, bool> tryHandleVerb) =>
        Run(() => ShellContextMenu.ShowForItems(window.Handle, paths, point.X, point.Y, IsShiftDown, IsDark(page), tryHandleVerb));

    public void ShowForFolder(FrameworkElement page, string folder, PointInt32 point, Func<string, bool> tryHandleVerb) =>
        Run(() => ShellContextMenu.ShowForFolder(window.Handle, folder, point.X, point.Y, IsShiftDown, IsDark(page), tryHandleVerb));

    private static bool IsDark(FrameworkElement page) => page.ActualTheme == ElementTheme.Dark;

    private void Run(Action show)
    {
        try
        {
            show();
        }
        catch (Exception exception) when (exception is COMException or ArgumentException
            or UnauthorizedAccessException or IOException or NotImplementedException)
        {
            shell.NotifyError(exception.Message, "Команда Windows не выполнена");
        }
    }
}

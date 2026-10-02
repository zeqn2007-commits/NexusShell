using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;

namespace Nexus.App.Services;

/// <summary>The main window's handle, XAML root and dispatcher for services that need them.</summary>
public sealed class WindowContext
{
    public IntPtr Handle { get; private set; }

    public XamlRoot? XamlRoot { get; private set; }

    public DispatcherQueue? Dispatcher { get; private set; }

    /// <summary>The mouse pointer in physical screen pixels.</summary>
    public static PointInt32 CursorPosition => GetCursorPos(out var point) ? new PointInt32(point.X, point.Y) : default;

    public void Attach(Window window, XamlRoot root)
    {
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        XamlRoot = root;
        Dispatcher = window.DispatcherQueue;
    }

    public void Post(Action action) => Dispatcher?.TryEnqueue(() => action());

    /// <summary>Converts a point of an element to physical screen pixels (for Win32 menus).</summary>
    public PointInt32 ToScreen(UIElement element, Point point)
    {
        var root = element.TransformToVisual(null).TransformPoint(point);
        var scale = XamlRoot?.RasterizationScale ?? 1.0;
        var native = new NativePoint { X = (int)Math.Round(root.X * scale), Y = (int)Math.Round(root.Y * scale) };
        ClientToScreen(Handle, ref native);
        return new PointInt32(native.X, native.Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
}

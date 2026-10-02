using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nexus.Core.Interop;

namespace Nexus.Core.Shell;

/// <summary>Hands items over to Windows: default app, "Open with", Properties, Explorer, terminal.</summary>
public static class ShellLauncher
{
    private const uint SeeMaskInvokeIdList = 0x0000000C;
    private const uint SeeMaskFlagNoUi = 0x00000400;
    private const int ShowNormal = 1;
    private const uint ShopFilePath = 0x2;

    public static void Open(string path)
    {
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty
        })?.Dispose();
    }

    public static void OpenWith(string path, IntPtr owner)
    {
        var info = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = SeeMaskInvokeIdList,
            Window = owner,
            Verb = "openas",
            File = path,
            Show = ShowNormal
        };
        if (!NativeMethods.ShellExecuteExW(ref info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Shows the standard Windows Properties dialog for a file, folder or drive.</summary>
    public static void ShowProperties(string path, IntPtr owner)
    {
        if (NativeMethods.SHObjectProperties(owner, ShopFilePath, path, null) == 0)
        {
            var info = new ShellExecuteInfo
            {
                Size = Marshal.SizeOf<ShellExecuteInfo>(),
                Mask = SeeMaskInvokeIdList | SeeMaskFlagNoUi,
                Window = owner,
                Verb = "properties",
                File = path,
                Show = ShowNormal
            };
            NativeMethods.ShellExecuteExW(ref info);
        }
    }

    public static void ShowInExplorer(string path)
    {
        var arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true })?.Dispose();
    }

    public static void OpenTerminal(string folder)
    {
        try
        {
            var terminal = new ProcessStartInfo("wt.exe") { UseShellExecute = true };
            terminal.ArgumentList.Add("-d");
            terminal.ArgumentList.Add(folder);
            Process.Start(terminal)?.Dispose();
        }
        catch (Win32Exception)
        {
            // Windows Terminal is not installed: fall back to PowerShell in that folder.
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                WorkingDirectory = folder
            })?.Dispose();
        }
    }

    public static void OpenUri(string uri) =>
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();

    /// <summary>Windows "Дополнительные параметры общего доступа": network discovery and file sharing.</summary>
    public static void OpenSharingSettings()
    {
        var control = new ProcessStartInfo("control.exe") { UseShellExecute = true };
        control.ArgumentList.Add("/name");
        control.ArgumentList.Add("Microsoft.NetworkAndSharingCenter");
        control.ArgumentList.Add("/page");
        control.ArgumentList.Add("Advanced");
        Process.Start(control)?.Dispose();
    }

    /// <summary>Launches through the shell with an explicit working folder (games, shortcuts).</summary>
    public static void Launch(string target, string? workingDirectory = null) =>
        Process.Start(new ProcessStartInfo(target)
        {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory ?? string.Empty
        })?.Dispose();
}

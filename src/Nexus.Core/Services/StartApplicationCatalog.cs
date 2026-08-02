using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Nexus.Core.Models;

[assembly: InternalsVisibleTo("Nexus.Core.Tests")]

namespace Nexus.Core.Services;

internal static class StartApplicationCatalog
{
    private const int MaximumApplications = 10_000;
    private const int MaximumDirectories = 2_500;
    private static readonly TimeSpan StartAppsTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartAppsCacheLifetime =
        TimeSpan.FromMinutes(2);
    private static readonly object StartAppsCacheLock = new();
    private static IReadOnlyList<StartApplicationInfo>? _cachedStartApps;
    private static DateTimeOffset _startAppsCachedAt;

    public static IReadOnlyList<string> GetDefaultStartMenuRoots()
    {
        return
        [
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        ];
    }

    public static IReadOnlyList<StartApplicationInfo> EnumeratePhysical(
        IEnumerable<string> roots,
        CancellationToken cancellationToken)
    {
        var applications = new List<StartApplicationInfo>();
        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(TryGetFullPath)
                     .Where(path => path is not null && Directory.Exists(path))
                     .Cast<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            pending.Enqueue(root);
        }

        while (pending.Count > 0
               && visited.Count < MaximumDirectories
               && applications.Count < MaximumApplications)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Dequeue();
            if (!visited.Add(current))
            {
                continue;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(
                             current,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (applications.Count >= MaximumApplications)
                    {
                        break;
                    }

                    var extension = Path.GetExtension(path).ToLowerInvariant();
                    if (extension is not (".lnk" or ".appref-ms" or ".url"))
                    {
                        continue;
                    }

                    var name = Path.GetFileNameWithoutExtension(path).Trim();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var file = new FileInfo(path);
                    var shortcut = extension == ".lnk"
                        ? TryResolveShortcut(path)
                        : null;
                    applications.Add(new StartApplicationInfo(
                        name,
                        file.FullName,
                        IconSourcePath: file.FullName,
                        ResolvedExecutablePath:
                            shortcut?.ResolvedExecutablePath,
                        Arguments: shortcut?.Arguments,
                        WorkingDirectory: shortcut?.WorkingDirectory,
                        SourcePath: file.FullName,
                        ModifiedAt: file.LastWriteTimeUtc,
                        IsSystemComponent: IsSystemStartMenuShortcut(
                            file.FullName,
                            shortcut?.ResolvedExecutablePath)));
                }

                foreach (var directory in Directory.EnumerateDirectories(
                             current,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    if (CanTraverseStartMenuDirectory(directory))
                    {
                        pending.Enqueue(directory);
                    }
                }
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException
                or IOException
                or ArgumentException)
            {
                // One inaccessible Start menu subtree must not hide the rest.
            }
        }

        return applications
            .GroupBy(GetStableIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(application => application.ModifiedAt)
                .ThenBy(
                    application => application.SourcePath,
                    StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(
                application => application.Name,
                StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(GetStableIdentity, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<StartApplicationInfo> GetStartApps(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (StartAppsCacheLock)
        {
            if (_cachedStartApps is not null
                && DateTimeOffset.UtcNow - _startAppsCachedAt
                    < StartAppsCacheLifetime)
            {
                return _cachedStartApps;
            }
        }

        var applications = QueryStartApps(cancellationToken);
        if (applications.Count > 0)
        {
            lock (StartAppsCacheLock)
            {
                _cachedStartApps = applications;
                _startAppsCachedAt = DateTimeOffset.UtcNow;
            }
        }

        return applications;
    }

    private static IReadOnlyList<StartApplicationInfo> QueryStartApps(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (!File.Exists(executable))
        {
            return [];
        }

        const string command =
            "$OutputEncoding=[Console]::OutputEncoding=" +
            "[System.Text.UTF8Encoding]::new($false);" +
            "Get-StartApps | Select-Object Name,AppID | " +
            "ConvertTo-Json -Compress";
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };
            process.StartInfo.ArgumentList.Add("-NoLogo");
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-Command");
            process.StartInfo.ArgumentList.Add(command);

            if (!process.Start())
            {
                return [];
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            var stopwatch = Stopwatch.StartNew();
            while (!process.WaitForExit(100))
            {
                if (cancellationToken.IsCancellationRequested
                    || stopwatch.Elapsed >= StartAppsTimeout)
                {
                    TryKill(process);
                    cancellationToken.ThrowIfCancellationRequested();
                    return [];
                }
            }

            Task.WhenAll(standardOutput, standardError)
                .GetAwaiter()
                .GetResult();
            if (process.ExitCode != 0)
            {
                return [];
            }

            return ParseStartAppsJson(standardOutput.Result);
        }
        catch (Exception exception) when (
            exception is Win32Exception
            or InvalidOperationException
            or IOException
            or JsonException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<StartApplicationInfo> ParseStartAppsJson(
        string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        IEnumerable<JsonElement> items = document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement.EnumerateArray(),
            JsonValueKind.Object => [document.RootElement],
            _ => []
        };
        var applications = new List<StartApplicationInfo>();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("Name", out var nameElement)
                || !item.TryGetProperty("AppID", out var appIdElement)
                || nameElement.ValueKind != JsonValueKind.String
                || appIdElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = nameElement.GetString()?.Trim();
            var appId = appIdElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(name)
                || !IsSafeApplicationId(appId))
            {
                continue;
            }

            var launchTarget = TryGetExistingFile(appId)
                ?? $@"shell:AppsFolder\{appId}";
            applications.Add(new StartApplicationInfo(
                name,
                launchTarget,
                appId,
                IconSourcePath: launchTarget,
                IsSystemComponent: IsKnownSystemApplicationId(appId!)));
        }

        return applications
            .GroupBy(
                application => application.ApplicationId,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(
                    application => application.Name,
                    StringComparer.CurrentCultureIgnoreCase)
                .First())
            .OrderBy(
                application => application.Name,
                StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(
                application => application.ApplicationId,
                StringComparer.OrdinalIgnoreCase)
            .Take(MaximumApplications)
            .ToArray();
    }

    internal static string GetStableIdentity(StartApplicationInfo application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (!string.IsNullOrWhiteSpace(application.ApplicationId))
        {
            return $"appid:{application.ApplicationId.Trim()}";
        }

        var sourcePath = TryGetIdentityPath(application.SourcePath);
        if (sourcePath is not null)
        {
            // Two shortcuts may intentionally launch the same executable with
            // different arguments or game profiles. Keep each shortcut as its
            // own application identity.
            return $"shortcut:{sourcePath}";
        }

        var executablePath = TryGetIdentityPath(
            application.ResolvedExecutablePath);
        if (executablePath is not null)
        {
            return $"exe:{executablePath}|args:{application.Arguments?.Trim()}";
        }

        return $"target:{application.LaunchTarget.Trim()}";
    }

    private static string? TryGetIdentityPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private static ResolvedShortcut? TryResolveShortcut(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        IShellLinkW? shellLink = null;
        try
        {
            shellLink = (IShellLinkW)(object)new ShellLink();
            ((IPersistFile)shellLink).Load(path, 0);

            var target = new StringBuilder(32_768);
            shellLink.GetPath(
                target,
                target.Capacity,
                out _,
                ShellLinkGetPathFlags.RawPath);
            var arguments = new StringBuilder(32_768);
            shellLink.GetArguments(arguments, arguments.Capacity);
            var workingDirectory = new StringBuilder(32_768);
            shellLink.GetWorkingDirectory(
                workingDirectory,
                workingDirectory.Capacity);

            var expandedTarget = Environment.ExpandEnvironmentVariables(
                target.ToString().Trim());
            return new ResolvedShortcut(
                TryGetExistingFile(expandedTarget),
                NullIfWhiteSpace(arguments.ToString()),
                TryGetExistingDirectory(
                    Environment.ExpandEnvironmentVariables(
                        workingDirectory.ToString().Trim())));
        }
        catch (Exception exception) when (
            exception is COMException
            or InvalidCastException
            or IOException
            or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (shellLink is not null
                && Marshal.IsComObject(shellLink))
            {
                Marshal.FinalReleaseComObject(shellLink);
            }
        }
    }

    private static string? TryGetExistingFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private static string? TryGetExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            return Directory.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsSafeApplicationId(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 2048
            && value.All(character => !char.IsControl(character))
            && !value.Contains('"');
    }

    private static bool IsSystemStartMenuShortcut(
        string sourcePath,
        string? executablePath)
    {
        var normalizedSource = sourcePath.Replace('/', '\\');
        string[] systemMenuFragments =
        [
            "\\Administrative Tools\\",
            "\\Windows Tools\\",
            "\\System Tools\\",
            "\\Администрирование\\",
            "\\Средства Windows\\",
            "\\Служебные — Windows\\"
        ];
        if (systemMenuFragments.Any(fragment => normalizedSource.Contains(
                fragment,
                StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var windowsDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        return !string.IsNullOrWhiteSpace(windowsDirectory)
            && executablePath.StartsWith(
                Path.Combine(windowsDirectory, "System32")
                    + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            && normalizedSource.Contains(
                "\\Programs\\Windows ",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownSystemApplicationId(string appId)
    {
        string[] exactIds =
        [
            "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
            "Microsoft.Windows.SecHealthUI_cw5n1h2txyewy!SecHealthUI",
            "Microsoft.Windows.Search_cw5n1h2txyewy!CortanaUI",
            "MicrosoftWindows.Client.CBS_cw5n1h2txyewy!CortanaUI",
            "Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy!App",
            "Microsoft.Windows.ShellExperienceHost_cw5n1h2txyewy!App"
        ];
        if (exactIds.Contains(appId, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] systemPrefixes =
        [
            "Microsoft.AAD.BrokerPlugin_",
            "Microsoft.AccountsControl_",
            "Microsoft.CredDialogHost_",
            "Microsoft.LockApp_",
            "Microsoft.Windows.CloudExperienceHost_",
            "Microsoft.Windows.OOBENetworkConnectionFlow_",
            "MicrosoftWindows.Client.FileExp_"
        ];
        return systemPrefixes.Any(prefix => appId.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase));
    }

    private static bool CanTraverseStartMenuDirectory(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            return !directory.Attributes.HasFlag(FileAttributes.ReparsePoint)
                && !directory.Attributes.HasFlag(FileAttributes.System);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static string? TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private static string? NullIfWhiteSpace(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or Win32Exception
            or NotSupportedException)
        {
        }
    }

    private sealed record ResolvedShortcut(
        string? ResolvedExecutablePath,
        string? Arguments,
        string? WorkingDirectory);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int maximumPath,
            out Win32FindData findData,
            ShellLinkGetPathFlags flags);

        void GetIdList(out IntPtr itemIdList);

        void SetIdList(IntPtr itemIdList);

        void GetDescription(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description,
            int maximumName);

        void SetDescription(
            [MarshalAs(UnmanagedType.LPWStr)] string description);

        void GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory,
            int maximumPath);

        void SetWorkingDirectory(
            [MarshalAs(UnmanagedType.LPWStr)] string directory);

        void GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments,
            int maximumPath);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCommand(out int showCommand);

        void SetShowCommand(int showCommand);

        void GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath,
            int maximumPath,
            out int iconIndex);

        void SetIconLocation(
            [MarshalAs(UnmanagedType.LPWStr)] string iconPath,
            int iconIndex);

        void SetRelativePath(
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            uint reserved);

        void Resolve(IntPtr windowHandle, uint flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [Flags]
    private enum ShellLinkGetPathFlags : uint
    {
        RawPath = 0x00000004
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public FileAttributes FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;
        public uint Reserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string AlternateFileName;
    }
}

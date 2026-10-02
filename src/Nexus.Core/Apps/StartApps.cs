using System.Runtime.InteropServices;
using Nexus.Core.Interop;
using Nexus.Core.IO;
using Nexus.Core.Threading;

namespace Nexus.Core.Apps;

/// <summary>An app from Start's "All apps" list.</summary>
/// <param name="AppId">Its AppsFolder name: an AUMID for Store apps, a (known-folder) path for desktop apps.</param>
public sealed record StartApp(string Name, string AppId)
{
    public const string AppsFolder = "shell:AppsFolder";

    /// <summary>The Shell path that launches the app and gives its icon and context menu.</summary>
    public string ShellPath => $@"{AppsFolder}\{AppId}";

    /// <summary>Store (packaged) apps are addressed by their AUMID: PackageFamilyName!AppId.</summary>
    public bool IsPackaged => AppId.Contains('!', StringComparison.Ordinal) && !AppId.Contains('\\', StringComparison.Ordinal);
}

/// <summary>
/// Start's "All apps" (shell:AppsFolder): desktop programs and Store apps alike, without uninstallers,
/// read-me files, web links and Windows' own plumbing.
/// </summary>
public static class StartApps
{
    private static readonly string[] JunkNameFragments =
    [
        "uninstall", "удалить", "удаление", "деинсталл", "readme", "read me", "release notes", "license", "лицензи", "website", "веб-сайт"
    ];

    private static readonly string[] SystemAppIdPrefixes =
    [
        "Microsoft.AAD.BrokerPlugin_", "Microsoft.AccountsControl_", "Microsoft.CredDialogHost_", "Microsoft.LockApp_",
        "Microsoft.Windows.CloudExperienceHost_", "Microsoft.Windows.OOBENetworkConnectionFlow_", "MicrosoftWindows.Client.FileExp_",
        "Microsoft.Windows.StartMenuExperienceHost_", "Microsoft.Windows.ShellExperienceHost_", "Microsoft.Windows.Search_",
        "MicrosoftWindows.Client.CBS_"
    ];

    public static Task<IReadOnlyList<StartApp>> LoadAsync(CancellationToken cancellationToken = default) =>
        StaThread.RunAsync(() => Load(cancellationToken), "Nexus Start Apps");

    /// <summary>False for entries that are not apps one would start: uninstallers, manuals, links.</summary>
    public static bool IsUsefulApp(string name, string appId)
    {
        var lowerName = name.ToLowerInvariant();
        return !JunkNameFragments.Any(fragment => lowerName.Contains(fragment, StringComparison.Ordinal))
            && !appId.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            && !appId.EndsWith(".url", StringComparison.OrdinalIgnoreCase)
            && !appId.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            && !appId.EndsWith(".chm", StringComparison.OrdinalIgnoreCase)
            && !appId.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            && !appId.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            && !SystemAppIdPrefixes.Any(prefix => appId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<StartApp> Load(CancellationToken cancellationToken)
    {
        if (NativeMethods.TryCreateShellItem(StartApp.AppsFolder) is not { } root)
        {
            return [];
        }

        var apps = new List<StartApp>();
        IEnumShellItems? enumerator = null;
        try
        {
            var handler = NativeMethods.EnumItemsHandlerId;
            var interfaceId = NativeMethods.EnumShellItemsInterfaceId;
            if (root.BindToHandler(IntPtr.Zero, ref handler, ref interfaceId, out var pointer) < 0)
            {
                return [];
            }

            try
            {
                enumerator = (IEnumShellItems)Marshal.GetObjectForIUnknown(pointer);
            }
            finally
            {
                Marshal.Release(pointer);
            }

            while (enumerator.Next(1, out var item, out var fetched) == NativeMethods.S_OK && fetched == 1 && item is not null)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = NativeMethods.GetDisplayName(item, Sigdn.NormalDisplay);
                    var appId = NativeMethods.GetDisplayName(item, Sigdn.ParentRelativeParsing);
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(appId) && IsUsefulApp(name, appId))
                    {
                        apps.Add(new StartApp(name.Trim(), appId));
                    }
                }
                finally
                {
                    NativeMethods.Release(item);
                }
            }
        }
        finally
        {
            NativeMethods.Release(enumerator);
            NativeMethods.Release(root);
        }

        return apps
            .DistinctBy(app => app.AppId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(app => app.Name, NaturalStringComparer.Instance)
            .ToArray();
    }
}

using System.Runtime.InteropServices;
using Nexus.Core.Interop;

namespace Nexus.Core.IO;

public enum KnownFolder
{
    Desktop,
    Downloads,
    Documents,
    Pictures,
    Music,
    Videos,
    Profile
}

public sealed record KnownFolderInfo(KnownFolder Folder, string Path, string Title, string Glyph);

/// <summary>
/// Locations of the Windows known folders. Uses SHGetKnownFolderPath so a
/// Downloads or Documents folder moved to another drive is still found.
/// </summary>
public static class KnownFolders
{
    private static readonly Lazy<IReadOnlyList<KnownFolderInfo>> All = new(Resolve);

    public static IReadOnlyList<KnownFolderInfo> UserFolders => All.Value;

    public static string GetPath(KnownFolder folder) =>
        All.Value.First(info => info.Folder == folder).Path;

    public static KnownFolderInfo? Find(string path)
    {
        var normalized = PathHelper.Normalize(path);
        return All.Value.FirstOrDefault(info => PathHelper.AreEqual(info.Path, normalized));
    }

    private static IReadOnlyList<KnownFolderInfo> Resolve()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            new(KnownFolder.Desktop, Query("B4BFCC3A-DB2C-424C-B029-7FE99A87C641", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)), "Рабочий стол", "\uE7F4"),
            new(KnownFolder.Downloads, Query("374DE290-123F-4565-9164-39C4925E467B", Path.Combine(profile, "Downloads")), "Загрузки", "\uE896"),
            new(KnownFolder.Documents, Query("FDD39AD0-238F-46AF-ADB4-6C85480369C7", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)), "Документы", "\uE8A5"),
            new(KnownFolder.Pictures, Query("33E28130-4E1E-4676-835A-98395C3BC3BB", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)), "Изображения", "\uEB9F"),
            new(KnownFolder.Music, Query("4BD8D571-6D19-48D3-BE97-422220080E43", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)), "Музыка", "\uEC4F"),
            new(KnownFolder.Videos, Query("18989B1D-99B5-455B-841C-AB7C74E4DDFC", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)), "Видео", "\uE714"),
            new(KnownFolder.Profile, profile, Path.GetFileName(profile), "\uE77B")
        ];
    }

    private static string Query(string folderId, string fallback)
    {
        var id = new Guid(folderId);
        if (NativeMethods.SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var pointer) >= 0)
        {
            var path = NativeMethods.TakeCoTaskString(pointer);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return PathHelper.Normalize(path);
            }
        }
        else if (pointer != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(pointer);
        }

        return PathHelper.Normalize(fallback);
    }
}

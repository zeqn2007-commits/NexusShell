using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Nexus.App.Models;
using Nexus.Core.IO;
using Nexus.Core.Shell;

namespace Nexus.App.ViewModels;

/// <summary>One row/tile in a folder view.</summary>
public sealed partial class FileItemViewModel : ObservableObject
{
    private bool _iconRequested;

    public FileItemViewModel(FileEntry entry, bool showExtension, string? location = null, FileKind? kind = null)
    {
        Entry = entry;
        Kind = kind ?? (entry.IsDirectory ? FileKind.Folder : FileKinds.FromExtension(entry.Extension));
        ShowsExtension = showExtension;
        DisplayName = entry.IsDirectory || showExtension || string.IsNullOrEmpty(entry.Extension)
            ? entry.Name
            : System.IO.Path.GetFileNameWithoutExtension(entry.Name);
        Location = location;
    }

    public bool ShowsExtension { get; }

    /// <summary>Screen readers announce list items by their string form.</summary>
    public override string ToString() => DisplayName;

    public FileEntry Entry { get; }

    public string Name => Entry.Name;

    public string Path => Entry.FullPath;

    public bool IsFolder => Entry.IsDirectory;

    public FileKind Kind { get; }

    public string DisplayName { get; }

    public string? Location { get; }

    public string TypeName => Kind == FileKind.Drive ? "Локальный диск" : ShellTypeNames.Get(Entry.Extension, Entry.IsDirectory);

    /// <summary>Third table column: the folder for search/recent/favourites, otherwise the type.</summary>
    public string TypeOrLocation => Location ?? TypeName;

    public string DisplayModified => Entry.Modified == default ? string.Empty : Formatting.DateTime(Entry.Modified);

    public string DisplayCreated => Entry.Created == default ? string.Empty : Formatting.DateTime(Entry.Created);

    public string DisplaySize => IsFolder ? string.Empty : Formatting.Size(Entry.Size);

    public bool IsDrive => Kind == FileKind.Drive;

    public string RelativeModified => Formatting.RelativeDate(Entry.Modified, DateTimeOffset.Now);

    public bool IsHidden => Entry.IsHidden;

    /// <summary>Hidden and cut items are drawn semi-transparent, like in Explorer.</summary>
    public double ContentOpacity => IsCut || IsHidden ? 0.55 : 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    public partial ImageSource? Icon { get; set; }

    public bool HasIcon => Icon is not null;

    /// <summary>Large icon or content thumbnail for tiles and the details pane.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLargeImage))]
    public partial ImageSource? LargeImage { get; set; }

    public bool HasLargeImage => LargeImage is not null;

    /// <summary>Pixel size of the largest <see cref="LargeImage"/> requested so far (0 = none).</summary>
    public int LargeImageSize { get; private set; }

    /// <summary>
    /// True when a large image of at least <paramref name="size"/> pixels still has to be loaded.
    /// Tiles ask for 96 px, the details pane for 256 px; a smaller picture is never stretched.
    /// </summary>
    public bool TryBeginLargeImageRequest(int size)
    {
        if (LargeImageSize >= size)
        {
            return false;
        }

        LargeImageSize = size;
        return true;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContentOpacity))]
    public partial bool IsCut { get; set; }

    [ObservableProperty]
    public partial bool IsRenaming { get; set; }

    /// <summary>True once an icon load was started, so scrolling back does not request it twice.</summary>
    public bool TryBeginIconRequest()
    {
        if (_iconRequested)
        {
            return false;
        }

        _iconRequested = true;
        return true;
    }
}

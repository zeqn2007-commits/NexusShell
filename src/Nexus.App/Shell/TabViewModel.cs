using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.Shell;

public sealed record BreadcrumbSegment(string Title, NavLocation Location)
{
    public override string ToString() => Title;
}

/// <summary>One browser tab: its location and its own back/forward history.</summary>
public sealed partial class TabViewModel : ObservableObject
{
    private readonly Stack<NavLocation> _back = new();
    private readonly Stack<NavLocation> _forward = new();

    public TabViewModel(NavLocation location)
    {
        Location = location;
        RebuildBreadcrumb();
    }

    [ObservableProperty]
    public partial NavLocation Location { get; private set; }

    public string Title => Location.Title;

    public string Glyph => Location.Glyph;

    public ObservableCollection<BreadcrumbSegment> Breadcrumb { get; } = [];

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public bool CanGoUp => Location.Kind == PageKind.Folder && Location.Path is not null;

    public string SearchPlaceholder => Location.Kind == PageKind.Home
        ? "Поиск в Nexus"
        : $"Поиск: {Location.Title}";

    public void Navigate(NavLocation location)
    {
        if (location == Location)
        {
            return;
        }

        _back.Push(Location);
        _forward.Clear();
        SetLocation(location);
    }

    public void GoBack()
    {
        if (_back.TryPop(out var previous))
        {
            _forward.Push(Location);
            SetLocation(previous);
        }
    }

    public void GoForward()
    {
        if (_forward.TryPop(out var next))
        {
            _back.Push(Location);
            SetLocation(next);
        }
    }

    public void GoUp()
    {
        if (Location.Kind != PageKind.Folder || Location.Path is not { } path)
        {
            return;
        }

        // From a drive root Explorer goes up to "This PC", from a network computer to "Network".
        Navigate(Nexus.Core.IO.PathHelper.GetParent(path) is { } parent
            ? NavLocation.ForFolder(parent)
            : NavLocation.FromTag(Nexus.Core.IO.PathHelper.IsNetworkComputer(path) ? "network" : "thispc"));
    }

    private void SetLocation(NavLocation location)
    {
        Location = location;
        RebuildBreadcrumb();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(SearchPlaceholder));
    }

    private void RebuildBreadcrumb()
    {
        Breadcrumb.Clear();
        if (Location.Kind != PageKind.Folder || Location.Path is null)
        {
            Breadcrumb.Add(new BreadcrumbSegment(Location.Title, Location));
            return;
        }

        // Known user folders read better as "Документы › Учёба" than as a full path.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var segments = new List<BreadcrumbSegment>();
        var current = Location.Path.TrimEnd('\\');
        while (!string.IsNullOrEmpty(current))
        {
            var known = NavLocation.KnownFolderTitle(current);
            if (known is not null)
            {
                segments.Add(new BreadcrumbSegment(known, NavLocation.ForFolder(current, known)));
                break;
            }

            if (string.Equals(current, profile, StringComparison.OrdinalIgnoreCase))
            {
                segments.Add(new BreadcrumbSegment(Path.GetFileName(current), NavLocation.ForFolder(current)));
                break;
            }

            if (Nexus.Core.IO.PathHelper.IsNetworkComputer(current))
            {
                // Network: "Сеть › SERVER › Share › …".
                var computer = NavLocation.ForFolder(current);
                segments.Add(new BreadcrumbSegment(computer.Title, computer));
                var network = NavLocation.FromTag("network");
                segments.Add(new BreadcrumbSegment(network.Title, network));
                break;
            }

            if (current.StartsWith(@"\\", StringComparison.Ordinal) && Nexus.Core.IO.PathHelper.IsDriveRoot(current))
            {
                var share = NavLocation.ForFolder(current);
                segments.Add(new BreadcrumbSegment(share.Title, share));
                current = Nexus.Core.IO.PathHelper.GetParent(current)!;
                continue;
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null)
            {
                // Drive root: "Этот компьютер › Локальный диск (C:) › …", like Explorer.
                var drive = NavLocation.ForFolder(current.EndsWith('\\') ? current : current + "\\");
                segments.Add(new BreadcrumbSegment(drive.Title, drive));
                var thisPc = NavLocation.FromTag("thispc");
                segments.Add(new BreadcrumbSegment(thisPc.Title, thisPc));
                break;
            }

            var name = Path.GetFileName(current);
            segments.Add(new BreadcrumbSegment(name, NavLocation.ForFolder(current, name)));
            current = parent;
        }

        segments.Reverse();
        foreach (var segment in segments)
        {
            Breadcrumb.Add(segment);
        }
    }
}

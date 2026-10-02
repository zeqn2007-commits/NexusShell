using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.App.Models;
using Nexus.Core.IO;

namespace Nexus.App.ViewModels;

/// <summary>"Этот компьютер": drives with their free space and the user's network locations.</summary>
public sealed partial class ThisPcViewModel : ObservableObject
{
    public ObservableCollection<DriveItem> Drives { get; } = [];

    public ObservableCollection<NetworkLocation> NetworkLocations { get; } = [];

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasNetworkLocations { get; set; }

    public async Task LoadAsync()
    {
        var drivesTask = Nexus.Core.IO.Drives.GetReadyAsync();
        var locationsTask = Nexus.Core.IO.NetworkLocations.GetAsync();
        var drives = await drivesTask;
        var locations = await locationsTask;

        Drives.Clear();
        foreach (var drive in drives)
        {
            Drives.Add(DriveItem.FromEntry(drive));
        }

        NetworkLocations.Clear();
        foreach (var location in locations)
        {
            NetworkLocations.Add(location);
        }

        HasNetworkLocations = NetworkLocations.Count > 0;
        var free = drives.Sum(drive => drive.FreeBytes);
        var total = drives.Sum(drive => drive.TotalBytes);
        Summary = $"{Environment.MachineName} · {Formatting.Count(drives.Count, "диск", "диска", "дисков")} · {Formatting.Size(free)} свободно из {Formatting.Size(total)}";
    }
}

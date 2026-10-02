using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.App.Models;
using Nexus.Core.IO;
using Nexus.Core.Shell;

namespace Nexus.App.ViewModels;

/// <summary>A computer, device or saved location on the "Сеть" page.</summary>
public sealed record NetworkTile(string Name, string Subtitle, string Target, FileKind Kind, bool IsFolder);

/// <summary>"Сеть": computers and devices in the local network, mapped drives and network locations.</summary>
public sealed partial class NetworkViewModel : ObservableObject
{
    public ObservableCollection<NetworkTile> Computers { get; } = [];

    public ObservableCollection<NetworkTile> Devices { get; } = [];

    public ObservableCollection<NetworkTile> Locations { get; } = [];

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial bool HasDevices { get; set; }

    [ObservableProperty]
    public partial bool HasLocations { get; set; }

    /// <summary>The scan finished without finding a single computer.</summary>
    [ObservableProperty]
    public partial bool NoComputersFound { get; set; }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        // Saved locations are local and fast: show them while the network is being scanned.
        var drives = await Nexus.Core.IO.Drives.GetReadyAsync();
        var locations = await NetworkLocations.GetAsync();
        Locations.Clear();
        foreach (var drive in drives.Where(drive => drive.Type == DriveType.Network))
        {
            Locations.Add(new NetworkTile(drive.DisplayName, $"{Formatting.Size(drive.FreeBytes)} свободно из {Formatting.Size(drive.TotalBytes)}",
                drive.RootPath, FileKind.Drive, IsFolder: true));
        }

        foreach (var location in locations)
        {
            Locations.Add(new NetworkTile(location.Name, location.Target, location.Target, FileKind.Folder, location.IsFolder));
        }

        HasLocations = Locations.Count > 0;

        IsScanning = true;
        NoComputersFound = false;
        try
        {
            var found = await NetworkNeighborhood.ScanAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Computers.Clear();
            Devices.Clear();
            foreach (var device in found)
            {
                if (device.Kind == NetworkDeviceKind.Computer)
                {
                    Computers.Add(new NetworkTile(device.Name, device.ParsingName, device.ParsingName, FileKind.Computer, IsFolder: true));
                }
                else
                {
                    Devices.Add(new NetworkTile(device.Name, "Устройство", device.ParsingName, FileKind.NetworkDevice, IsFolder: false));
                }
            }
        }
        catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException)
        {
            // Network discovery off or blocked: the empty state explains what to do.
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsScanning = false;
                HasDevices = Devices.Count > 0;
                NoComputersFound = Computers.Count == 0;
            }
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Nexus.Core.IO;

namespace Nexus.App.Models;

public sealed partial class DriveItem : ObservableObject
{
    public required string Name { get; init; }

    public required string RootPath { get; init; }

    public long TotalBytes { get; init; }

    public long FreeBytes { get; init; }

    public DriveType Type { get; init; } = DriveType.Fixed;

    /// <summary>"NTFS · Локальный диск".</summary>
    public string Details { get; init; } = string.Empty;

    public double UsedFraction => TotalBytes <= 0 ? 0 : 1 - (double)FreeBytes / TotalBytes;

    public double UsedPercent => UsedFraction * 100;

    public bool IsAlmostFull => UsedFraction >= 0.9;

    public string Summary => $"{Formatting.Size(FreeBytes)} свободно из {Formatting.Size(TotalBytes)}";

    /// <summary>The drive's own Windows icon (the system drive carries the Windows logo).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    public partial ImageSource? Icon { get; set; }

    public bool HasIcon => Icon is not null;

    public static DriveItem FromEntry(DriveEntry drive) => new()
    {
        Name = drive.DisplayName,
        RootPath = drive.RootPath,
        TotalBytes = drive.TotalBytes,
        FreeBytes = drive.FreeBytes,
        Type = drive.Type,
        Details = string.IsNullOrEmpty(drive.Format) ? drive.TypeName : $"{drive.Format} · {drive.TypeName}"
    };
}

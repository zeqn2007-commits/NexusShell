namespace Nexus.Core.IO;

public sealed record DriveEntry(
    string RootPath,
    string Label,
    DriveType Type,
    string Format,
    long TotalBytes,
    long FreeBytes)
{
    public string Letter => RootPath.TrimEnd('\\');

    public string DisplayName => string.IsNullOrWhiteSpace(Label)
        ? $"{TypeName} ({Letter})"
        : $"{Label} ({Letter})";

    public double UsedFraction => TotalBytes <= 0 ? 0 : 1 - (double)FreeBytes / TotalBytes;

    public string Glyph => Type switch
    {
        DriveType.Removable => "\uE88E",
        DriveType.Network => "\uE8CE",
        DriveType.CDRom => "\uE958",
        _ => "\uEDA2"
    };

    /// <summary>What Explorer calls the drive when it has no label: «Локальный диск», «Съёмный диск»…</summary>
    public string TypeName => Type switch
    {
        DriveType.Removable => "Съёмный диск",
        DriveType.Network => "Сетевой диск",
        DriveType.CDRom => "Дисковод",
        _ => "Локальный диск"
    };
}

public static class Drives
{
    public static IReadOnlyList<DriveEntry> GetReady(bool includeNetwork = true)
    {
        var result = new List<DriveEntry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady
                    || drive.DriveType is DriveType.NoRootDirectory or DriveType.Unknown or DriveType.Ram
                    || (!includeNetwork && drive.DriveType == DriveType.Network))
                {
                    continue;
                }

                result.Add(new DriveEntry(
                    drive.RootDirectory.FullName,
                    drive.VolumeLabel,
                    drive.DriveType,
                    drive.DriveFormat,
                    drive.TotalSize,
                    drive.AvailableFreeSpace));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A drive can disappear (USB unplugged) between enumeration and query.
            }
        }

        return result;
    }

    public static Task<IReadOnlyList<DriveEntry>> GetReadyAsync(bool includeNetwork = true) =>
        Task.Run(() => GetReady(includeNetwork));
}

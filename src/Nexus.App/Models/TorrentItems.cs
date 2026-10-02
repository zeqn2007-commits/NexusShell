using Nexus.Core.IO;
using Nexus.Core.Torrents;

namespace Nexus.App.Models;

/// <summary>A torrent in a client and where its files are.</summary>
public sealed class TorrentDownloadItem(TorrentDownload download, bool isFolder, bool exists)
{
    public TorrentDownload Download { get; } = download;

    public string Name => Download.Name;

    public bool IsFolder => isFolder;

    public bool CanOpen => exists;

    public bool IsComplete => Download.IsComplete;

    public bool IsDownloading => !Download.IsComplete;

    public bool ShowDoneMark => IsComplete && exists;

    /// <summary>0…100 for the progress bar.</summary>
    public double Percent => Download.Progress * 100;

    public string Glyph => !exists ? "" : isFolder ? "" : "";

    public string Status => IsDownloading ? $"{Math.Floor(Percent):0} %" : exists ? "Завершено" : "Файлы не найдены";

    public string Details => string.Join(" · ", new[] { Download.Client, Formatting.Size(Download.Size), Where }
        .Where(part => !string.IsNullOrEmpty(part)));

    private string? Where => Download.ContentPath is { } path && PathHelper.GetParent(path) is { } parent ? Formatting.Location(parent) : null;
}

/// <summary>A .torrent file in Downloads or on the desktop; <paramref name="added"/> is its download when a client already has it.</summary>
public sealed class TorrentFileItem(TorrentFile file, TorrentDownload? added, string? clientName)
{
    public TorrentFile File { get; } = file;

    public string Name => File.Name;

    public string FileName => Path.GetFileName(File.Path);

    public bool IsAdded => added is not null;

    public bool IsNew => added is null;

    public string AddedText => added is null ? string.Empty : added.IsComplete ? $"Скачан в {added.Client}" : $"Уже в {added.Client}";

    public string OpenText => clientName is null ? "Открыть" : $"Открыть в {clientName}";

    public string Details => File.InfoHash is null
        ? $"Торрент-файл повреждён · {Formatting.RelativeDate(File.Modified, DateTimeOffset.Now)}"
        : string.Join(" · ", "Торрент-файл", Formatting.Size(File.Size), Formatting.Count(File.FileCount, "файл", "файла", "файлов"),
            Formatting.RelativeDate(File.Modified, DateTimeOffset.Now));

    public string Location => Formatting.Location(PathHelper.GetParent(File.Path) ?? File.Path);
}

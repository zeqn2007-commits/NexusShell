using System.Numerics;
using System.Security.Cryptography;

namespace Nexus.Core.Torrents;

/// <summary>What a .torrent file describes.</summary>
/// <param name="InfoHash">SHA-1 of the "info" dictionary in lowercase hex: the torrent's identity in every client.</param>
public sealed record TorrentInfo(string Name, long Size, int FileCount, int PieceCount, string InfoHash);

/// <summary>A torrent added to a client.</summary>
/// <param name="ContentPath">Where the downloaded content is (folder or file), when known.</param>
/// <param name="Progress">0…1.</param>
public sealed record TorrentDownload(
    string Name,
    string Client,
    string? ContentPath,
    long? Size,
    double Progress,
    bool IsComplete,
    DateTimeOffset? Added,
    string? InfoHash);

/// <summary>A .torrent file lying in the user's folders.</summary>
public sealed record TorrentFile(string Path, string Name, long Size, int FileCount, DateTimeOffset Modified, string? InfoHash);

/// <summary>
/// Downloads of BitTorrent, µTorrent, qBittorrent, Transmission and Deluge, and .torrent files in the user's folders.
/// Every client keeps its own resume data, but all of it is bencoded.
/// </summary>
public static class TorrentLibrary
{
    private const int TransmissionBlockSize = 16 * 1024;

    public static TorrentInfo? ReadInfo(string torrentPath) =>
        Bencode.TryReadFile(torrentPath) is { } data ? ParseInfo(data) : null;

    public static TorrentInfo? ParseInfo(byte[] data)
    {
        var info = Bencode.TryDecode(data)?["info"];
        var name = info?["name.utf-8"]?.Text ?? info?["name"]?.Text;
        if (info?.Dictionary is null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var hash = Convert.ToHexStringLower(SHA1.HashData(data.AsSpan(info.Offset, info.Length)));
        var pieces = (info["pieces"]?.Bytes?.Length ?? 0) / 20;
        return info["files"]?.List is { } files
            ? new TorrentInfo(name, files.Sum(file => file["length"]?.Integer ?? 0), files.Count, pieces, hash)
            : new TorrentInfo(name, info["length"]?.Integer ?? 0, 1, pieces, hash);
    }

    public static IReadOnlyList<TorrentDownload> LoadDownloads(string appData, string localAppData) =>
        new[]
        {
            ReadUTorrentFamily(Path.Combine(appData, "BitTorrent"), "BitTorrent"),
            ReadUTorrentFamily(Path.Combine(appData, "uTorrent"), "µTorrent"),
            ReadUTorrentFamily(Path.Combine(appData, "BitTorrent Web"), "BitTorrent Web"),
            ReadUTorrentFamily(Path.Combine(appData, "uTorrent Web"), "µTorrent Web"),
            ReadQBittorrent(Path.Combine(localAppData, "qBittorrent", "BT_backup")),
            ReadTransmission(Path.Combine(localAppData, "transmission")),
            ReadTransmission(Path.Combine(appData, "Transmission")),
            ReadDeluge(Path.Combine(appData, "deluge", "state"))
        }
        .SelectMany(downloads => downloads)
        .DistinctBy(download => (download.Client, download.InfoHash ?? download.Name))
        .ToArray();

    /// <summary>
    /// BitTorrent and µTorrent keep resume.dat: a dictionary per added .torrent (stored next to it)
    /// with "caption", "path" (the content), "info" (hash), "have" (piece bitfield), "added_on" and "completed_on".
    /// </summary>
    public static IReadOnlyList<TorrentDownload> ReadUTorrentFamily(string folder, string client)
    {
        var resume = Bencode.TryDecodeFile(Path.Combine(folder, "resume.dat"), 256L * 1024 * 1024)?.Dictionary;
        if (resume is null)
        {
            return [];
        }

        var downloads = new List<TorrentDownload>();
        foreach (var (key, value) in resume)
        {
            // Besides torrents the file holds service keys such as ".fileguard" and "rec".
            if (!key.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) || value.Dictionary is null)
            {
                continue;
            }

            var info = ReadInfo(Path.Combine(folder, key));
            var progress = value["completed_on"]?.Integer > 0
                ? 1
                : BitfieldProgress(value["have"]?.Bytes, info?.PieceCount) ?? BytesProgress(value["downloaded"]?.Integer, info?.Size);
            downloads.Add(new TorrentDownload(
                value["caption"]?.Text is { Length: > 0 } caption ? caption : info?.Name ?? Path.GetFileNameWithoutExtension(key),
                client,
                value["path"]?.Text,
                info?.Size,
                progress,
                progress >= 1,
                FromUnix(value["added_on"]?.Integer),
                value["info"]?.Bytes is { Length: 20 } hash ? Convert.ToHexStringLower(hash) : info?.InfoHash));
        }

        return downloads;
    }

    /// <summary>qBittorrent: BT_backup/{hash}.fastresume (libtorrent resume data) next to {hash}.torrent.</summary>
    public static IReadOnlyList<TorrentDownload> ReadQBittorrent(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var downloads = new List<TorrentDownload>();
        foreach (var resumePath in SafeFiles(folder, "*.fastresume"))
        {
            if (Bencode.TryDecodeFile(resumePath) is { Dictionary: not null } resume)
            {
                var hash = Path.GetFileNameWithoutExtension(resumePath);
                downloads.Add(FromLibtorrent(resume, ReadInfo(Path.ChangeExtension(resumePath, ".torrent")), "qBittorrent", hash));
            }
        }

        return downloads;
    }

    /// <summary>Deluge: state/torrents.fastresume maps each info hash to bencoded libtorrent resume data; state/{hash}.torrent lies next to it.</summary>
    public static IReadOnlyList<TorrentDownload> ReadDeluge(string stateFolder)
    {
        var all = Bencode.TryDecodeFile(Path.Combine(stateFolder, "torrents.fastresume"), 256L * 1024 * 1024)?.Dictionary;
        if (all is null)
        {
            return [];
        }

        var downloads = new List<TorrentDownload>();
        foreach (var (hash, value) in all)
        {
            var resume = value.Dictionary is not null ? value : value.Bytes is { } nested ? Bencode.TryDecode(nested) : null;
            if (IsInfoHash(hash) && resume?.Dictionary is not null)
            {
                downloads.Add(FromLibtorrent(resume, ReadInfo(Path.Combine(stateFolder, $"{hash}.torrent")), "Deluge", hash));
            }
        }

        return downloads;
    }

    /// <summary>
    /// Transmission: resume/{name}.resume next to torrents/{name}.torrent, with "destination", "name", "added-date",
    /// "done-date" and "progress" (bitfields of pieces or 16 KiB blocks, or the words "all" and "none").
    /// </summary>
    public static IReadOnlyList<TorrentDownload> ReadTransmission(string folder)
    {
        var resumeFolder = Path.Combine(folder, "resume");
        if (!Directory.Exists(resumeFolder))
        {
            return [];
        }

        var downloads = new List<TorrentDownload>();
        foreach (var resumePath in SafeFiles(resumeFolder, "*.resume"))
        {
            var resume = Bencode.TryDecodeFile(resumePath);
            if (resume?.Dictionary is null)
            {
                continue;
            }

            var baseName = Path.GetFileNameWithoutExtension(resumePath);
            var info = ReadInfo(Path.Combine(folder, "torrents", baseName + ".torrent"));
            var name = resume["name"]?.Text is { Length: > 0 } own ? own : info?.Name ?? baseName;
            var destination = resume["destination"]?.Text;
            var state = resume["progress"];
            int? blocks = info?.Size is > 0 and var size ? (int)Math.Min(int.MaxValue, (size + TransmissionBlockSize - 1) / TransmissionBlockSize) : null;
            var progress = resume["done-date"]?.Integer > 0 || state?["have"]?.Text == "all"
                ? 1
                : TransmissionBits(state?["pieces"], info?.PieceCount)
                  ?? TransmissionBits(state?["blocks"], blocks)
                  ?? BytesProgress(resume["downloaded"]?.Integer, info?.Size);
            downloads.Add(new TorrentDownload(
                name,
                "Transmission",
                string.IsNullOrEmpty(destination) ? null : Path.Combine(destination, info?.Name ?? name),
                info?.Size,
                progress,
                progress >= 1,
                FromUnix(resume["added-date"]?.Integer),
                info?.InfoHash ?? (IsInfoHash(baseName) ? baseName.ToLowerInvariant() : null)));
        }

        return downloads;
    }

    /// <summary>.torrent files in the given folders and one level below.</summary>
    public static IReadOnlyList<TorrentFile> FindFiles(IEnumerable<string> folders)
    {
        var files = new List<TorrentFile>();
        foreach (var folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var paths = SafeFiles(folder, "*.torrent").Concat(SafeDirectories(folder).SelectMany(child => SafeFiles(child, "*.torrent")));
            foreach (var path in paths)
            {
                var info = ReadInfo(path);
                files.Add(new TorrentFile(
                    path,
                    info?.Name ?? Path.GetFileNameWithoutExtension(path),
                    info?.Size ?? 0,
                    info?.FileCount ?? 0,
                    File.GetLastWriteTime(path),
                    info?.InfoHash));
            }
        }

        return files.OrderByDescending(file => file.Modified).ToArray();
    }

    /// <summary>libtorrent resume data, as qBittorrent and Deluge store it.</summary>
    private static TorrentDownload FromLibtorrent(BValue resume, TorrentInfo? info, string client, string fileHash)
    {
        var name = resume["qBt-name"]?.Text is { Length: > 0 } custom ? custom : info?.Name ?? fileHash;
        var savePath = resume["save_path"]?.Text ?? resume["qBt-savePath"]?.Text;
        var progress = resume["completed_time"]?.Integer > 0
            ? 1
            : PiecesProgress(resume["pieces"]?.Bytes) ?? BytesProgress(resume["total_downloaded"]?.Integer, info?.Size);
        return new TorrentDownload(
            name,
            client,
            string.IsNullOrEmpty(savePath) ? null : Path.Combine(savePath, info?.Name ?? name),
            info?.Size,
            progress,
            progress >= 1,
            FromUnix(resume["added_time"]?.Integer),
            resume["info-hash"]?.Bytes is { Length: 20 } hash ? Convert.ToHexStringLower(hash)
                : IsInfoHash(fileHash) ? fileHash.ToLowerInvariant()
                : info?.InfoHash);
    }

    private static bool IsInfoHash(string text) => text.Length == 40 && text.All(char.IsAsciiHexDigit);

    /// <summary>Transmission writes a bitfield, or "all" / "none" instead of one.</summary>
    private static double? TransmissionBits(BValue? value, int? count) => value?.Text switch
    {
        "all" => 1,
        "none" => 0,
        _ => BitfieldProgress(value?.Bytes, count)
    };

    /// <summary>A bitfield with one bit per piece (µTorrent's "have") or per block (Transmission).</summary>
    private static double? BitfieldProgress(byte[]? bits, int? count)
    {
        if (bits is null || count is not > 0)
        {
            return null;
        }

        var set = 0;
        foreach (var value in bits)
        {
            set += BitOperations.PopCount(value);
        }

        return Math.Clamp(set / (double)count.Value, 0, 1);
    }

    /// <summary>libtorrent's "pieces": one byte per piece, bit 0 set when the piece is there.</summary>
    private static double? PiecesProgress(byte[]? pieces) =>
        pieces is { Length: > 0 } ? pieces.Count(value => (value & 1) != 0) / (double)pieces.Length : null;

    private static double BytesProgress(long? downloaded, long? size) =>
        downloaded is { } bytes && size is > 0 ? Math.Clamp(bytes / (double)size.Value, 0, 1) : 0;

    private static DateTimeOffset? FromUnix(long? seconds) => seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;

    private static string[] SafeFiles(string folder, string pattern)
    {
        try
        {
            return Directory.GetFiles(folder, pattern);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string[] SafeDirectories(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).GetDirectories()
                .Where(child => (child.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0)
                .Select(child => child.FullName)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

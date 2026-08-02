using System.Text;
using Nexus.Core.Models;

namespace Nexus.Core.Services;

internal static class TorrentMetadataReader
{
    private const int MaximumTorrentFileSize = 8 * 1024 * 1024;
    private const int MaximumNestingDepth = 32;
    private const int MaximumTrackers = 32;
    private const int MaximumContentFiles = 1_000_000;

    public static string? TryReadContentName(string path)
    {
        return TryRead(path)?.ContentName;
    }

    public static TorrentFileMetadata? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (stream.Length <= 0
                || stream.Length > MaximumTorrentFileSize)
            {
                return null;
            }

            var data = new byte[(int)stream.Length];
            stream.ReadExactly(data);
            return Parse(data);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return null;
        }
    }

    private static TorrentFileMetadata? Parse(byte[] data)
    {
        var offset = 0;
        if (!Consume(data, ref offset, (byte)'d'))
        {
            return null;
        }

        string? announce = null;
        string? fallbackComment = null;
        string? utf8Comment = null;
        string? createdBy = null;
        long? creationDate = null;
        InfoMetadata? info = null;
        var trackers = new List<string>();

        while (offset < data.Length && data[offset] != (byte)'e')
        {
            var key = ReadString(data, ref offset);
            if (key is null)
            {
                return null;
            }

            switch (key)
            {
                case "announce":
                    announce = ReadString(data, ref offset);
                    if (announce is null)
                    {
                        return null;
                    }

                    break;
                case "announce-list":
                    if (!ReadTrackerCollection(
                            data,
                            ref offset,
                            trackers,
                            depth: 1))
                    {
                        return null;
                    }

                    break;
                case "comment":
                    fallbackComment = ReadString(data, ref offset);
                    if (fallbackComment is null)
                    {
                        return null;
                    }

                    break;
                case "comment.utf-8":
                    utf8Comment = ReadString(data, ref offset);
                    if (utf8Comment is null)
                    {
                        return null;
                    }

                    break;
                case "created by":
                    createdBy = ReadString(data, ref offset);
                    if (createdBy is null)
                    {
                        return null;
                    }

                    break;
                case "creation date":
                    creationDate = ReadInteger(data, ref offset);
                    if (creationDate is null)
                    {
                        return null;
                    }

                    break;
                case "info":
                    info = ReadInfo(data, ref offset);
                    if (info is null)
                    {
                        return null;
                    }

                    break;
                default:
                    if (!SkipValue(data, ref offset, 0))
                    {
                        return null;
                    }

                    break;
            }
        }

        if (!Consume(data, ref offset, (byte)'e')
            || info is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(announce))
        {
            trackers.Insert(0, announce);
        }

        return new TorrentFileMetadata(
            NullIfWhiteSpace(info.Utf8Name)
            ?? NullIfWhiteSpace(info.FallbackName),
            info.FileCount,
            info.TotalSize,
            TryConvertUnixTime(creationDate),
            NullIfWhiteSpace(createdBy),
            NullIfWhiteSpace(utf8Comment)
            ?? NullIfWhiteSpace(fallbackComment),
            trackers
                .Select(NormalizeTracker)
                .Where(value => value is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumTrackers)
                .ToArray());
    }

    private static InfoMetadata? ReadInfo(byte[] data, ref int offset)
    {
        if (!Consume(data, ref offset, (byte)'d'))
        {
            return null;
        }

        string? fallbackName = null;
        string? utf8Name = null;
        int? fileCount = null;
        long? totalSize = null;
        while (offset < data.Length && data[offset] != (byte)'e')
        {
            var key = ReadString(data, ref offset);
            if (key is null)
            {
                return null;
            }

            switch (key)
            {
                case "name":
                    fallbackName = ReadString(data, ref offset);
                    if (fallbackName is null)
                    {
                        return null;
                    }

                    break;
                case "name.utf-8":
                    utf8Name = ReadString(data, ref offset);
                    if (utf8Name is null)
                    {
                        return null;
                    }

                    break;
                case "length":
                    var length = ReadInteger(data, ref offset);
                    if (length is null || length < 0)
                    {
                        return null;
                    }

                    fileCount = 1;
                    totalSize = length;
                    break;
                case "files":
                    var files = ReadFiles(data, ref offset);
                    if (files is null)
                    {
                        return null;
                    }

                    fileCount = files.Value.Count;
                    totalSize = files.Value.TotalSize;
                    break;
                default:
                    if (!SkipValue(data, ref offset, 1))
                    {
                        return null;
                    }

                    break;
            }
        }

        return Consume(data, ref offset, (byte)'e')
            ? new InfoMetadata(
                fallbackName,
                utf8Name,
                fileCount,
                totalSize)
            : null;
    }

    private static FileAggregate? ReadFiles(byte[] data, ref int offset)
    {
        if (!Consume(data, ref offset, (byte)'l'))
        {
            return null;
        }

        var count = 0;
        long totalSize = 0;
        while (offset < data.Length && data[offset] != (byte)'e')
        {
            if (count >= MaximumContentFiles
                || !Consume(data, ref offset, (byte)'d'))
            {
                return null;
            }

            long? length = null;
            while (offset < data.Length && data[offset] != (byte)'e')
            {
                var key = ReadString(data, ref offset);
                if (key is null)
                {
                    return null;
                }

                if (key == "length")
                {
                    length = ReadInteger(data, ref offset);
                    if (length is null || length < 0)
                    {
                        return null;
                    }
                }
                else if (!SkipValue(data, ref offset, 2))
                {
                    return null;
                }
            }

            if (!Consume(data, ref offset, (byte)'e')
                || length is null)
            {
                return null;
            }

            try
            {
                totalSize = checked(totalSize + length.Value);
            }
            catch (OverflowException)
            {
                return null;
            }

            count++;
        }

        return Consume(data, ref offset, (byte)'e')
            ? new FileAggregate(count, totalSize)
            : null;
    }

    private static bool ReadTrackerCollection(
        byte[] data,
        ref int offset,
        ICollection<string> trackers,
        int depth)
    {
        if (depth > MaximumNestingDepth || offset >= data.Length)
        {
            return false;
        }

        if (data[offset] == (byte)'l')
        {
            offset++;
            while (offset < data.Length && data[offset] != (byte)'e')
            {
                if (!ReadTrackerCollection(
                        data,
                        ref offset,
                        trackers,
                        depth + 1))
                {
                    return false;
                }
            }

            return Consume(data, ref offset, (byte)'e');
        }

        var tracker = ReadString(data, ref offset);
        if (tracker is null)
        {
            return false;
        }

        if (trackers.Count < MaximumTrackers
            && !string.IsNullOrWhiteSpace(tracker))
        {
            trackers.Add(tracker);
        }

        return true;
    }

    private static bool SkipValue(byte[] data, ref int offset, int depth)
    {
        if (depth > MaximumNestingDepth || offset >= data.Length)
        {
            return false;
        }

        if (data[offset] == (byte)'i')
        {
            return ReadInteger(data, ref offset) is not null;
        }

        if (data[offset] is (byte)'l' or (byte)'d')
        {
            var dictionary = data[offset] == (byte)'d';
            offset++;
            while (offset < data.Length && data[offset] != (byte)'e')
            {
                if (dictionary && ReadString(data, ref offset) is null)
                {
                    return false;
                }

                if (!SkipValue(data, ref offset, depth + 1))
                {
                    return false;
                }
            }

            return Consume(data, ref offset, (byte)'e');
        }

        return ReadBytes(data, ref offset) is not null;
    }

    private static long? ReadInteger(byte[] data, ref int offset)
    {
        if (!Consume(data, ref offset, (byte)'i')
            || offset >= data.Length)
        {
            return null;
        }

        var negative = data[offset] == (byte)'-';
        if (negative)
        {
            offset++;
        }

        if (offset >= data.Length
            || !char.IsAsciiDigit((char)data[offset]))
        {
            return null;
        }

        long value = 0;
        while (offset < data.Length && data[offset] != (byte)'e')
        {
            if (!char.IsAsciiDigit((char)data[offset]))
            {
                return null;
            }

            try
            {
                value = checked(value * 10 + data[offset] - (byte)'0');
            }
            catch (OverflowException)
            {
                return null;
            }

            offset++;
        }

        if (!Consume(data, ref offset, (byte)'e'))
        {
            return null;
        }

        try
        {
            return negative ? checked(-value) : value;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static string? ReadString(byte[] data, ref int offset)
    {
        var bytes = ReadBytes(data, ref offset);
        return bytes is null
            ? null
            : Encoding.UTF8.GetString(bytes);
    }

    private static byte[]? ReadBytes(byte[] data, ref int offset)
    {
        if (offset >= data.Length || !char.IsAsciiDigit((char)data[offset]))
        {
            return null;
        }

        var length = 0;
        while (offset < data.Length && data[offset] != (byte)':')
        {
            if (!char.IsAsciiDigit((char)data[offset]))
            {
                return null;
            }

            try
            {
                length = checked(length * 10 + data[offset] - (byte)'0');
            }
            catch (OverflowException)
            {
                return null;
            }

            offset++;
        }

        if (!Consume(data, ref offset, (byte)':')
            || length < 0
            || offset + length > data.Length)
        {
            return null;
        }

        var result = data.AsSpan(offset, length).ToArray();
        offset += length;
        return result;
    }

    private static string? NormalizeTracker(string? value)
    {
        var tracker = NullIfWhiteSpace(value);
        if (tracker is null
            || tracker.Length > 2048
            || !Uri.TryCreate(tracker, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "udp"))
        {
            return null;
        }

        return uri.AbsoluteUri;
    }

    private static DateTimeOffset? TryConvertUnixTime(long? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(value.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool Consume(byte[] data, ref int offset, byte value)
    {
        if (offset >= data.Length || data[offset] != value)
        {
            return false;
        }

        offset++;
        return true;
    }

    private sealed record InfoMetadata(
        string? FallbackName,
        string? Utf8Name,
        int? FileCount,
        long? TotalSize);

    private readonly record struct FileAggregate(int Count, long TotalSize);
}

using System.Text;

namespace Nexus.Core.Torrents;

/// <summary>A bencoded value: an integer, a byte string, a list or a dictionary.</summary>
public sealed class BValue
{
    public long? Integer { get; private init; }

    public byte[]? Bytes { get; private init; }

    public IReadOnlyList<BValue>? List { get; private init; }

    public IReadOnlyDictionary<string, BValue>? Dictionary { get; private init; }

    /// <summary>Where the value lies in the decoded data: a torrent's info hash is the SHA-1 of its "info" bytes.</summary>
    public int Offset { get; private init; }

    public int Length { get; private init; }

    public string? Text => Bytes is null ? null : Encoding.UTF8.GetString(Bytes);

    public BValue? this[string key] => Dictionary is not null && Dictionary.TryGetValue(key, out var value) ? value : null;

    internal static BValue FromInteger(long value, int offset, int length) => new() { Integer = value, Offset = offset, Length = length };

    internal static BValue FromBytes(byte[] value, int offset, int length) => new() { Bytes = value, Offset = offset, Length = length };

    internal static BValue FromList(IReadOnlyList<BValue> value, int offset, int length) => new() { List = value, Offset = offset, Length = length };

    internal static BValue FromDictionary(IReadOnlyDictionary<string, BValue> value, int offset, int length) =>
        new() { Dictionary = value, Offset = offset, Length = length };
}

/// <summary>Decoder for bencoding, the format of .torrent files and of torrent clients' resume data.</summary>
public static class Bencode
{
    private const int MaximumDepth = 64;

    /// <exception cref="InvalidDataException">The data is not valid bencoding.</exception>
    public static BValue Decode(byte[] data)
    {
        var index = 0;
        try
        {
            return Read(data, ref index, depth: 0);
        }
        catch (IndexOutOfRangeException exception)
        {
            throw new InvalidDataException("Bencoded data ends too early.", exception);
        }
    }

    /// <summary>Null instead of an exception for broken data.</summary>
    public static BValue? TryDecode(byte[] data)
    {
        try
        {
            return Decode(data);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The bytes of a file no larger than <paramref name="maximumBytes"/>; null when it is missing, empty or too big.</summary>
    public static byte[]? TryReadFile(string path, long maximumBytes = 16 * 1024 * 1024)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > maximumBytes)
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var data = new byte[info.Length];
            stream.ReadExactly(data);
            return data;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static BValue? TryDecodeFile(string path, long maximumBytes = 16 * 1024 * 1024) =>
        TryReadFile(path, maximumBytes) is { } data ? TryDecode(data) : null;

    private static BValue Read(byte[] data, ref int index, int depth)
    {
        if (depth > MaximumDepth)
        {
            throw new InvalidDataException("Bencoded data is nested too deeply.");
        }

        var start = index;
        switch (data[index])
        {
            case (byte)'i':
                index++;
                var end = Array.IndexOf(data, (byte)'e', index);
                if (end < 0 || !long.TryParse(Encoding.ASCII.GetString(data, index, end - index), out var number))
                {
                    throw new InvalidDataException("Bad bencoded integer.");
                }

                index = end + 1;
                return BValue.FromInteger(number, start, index - start);

            case (byte)'l':
                index++;
                var list = new List<BValue>();
                while (data[index] != (byte)'e')
                {
                    list.Add(Read(data, ref index, depth + 1));
                }

                index++;
                return BValue.FromList(list, start, index - start);

            case (byte)'d':
                index++;
                var dictionary = new Dictionary<string, BValue>(StringComparer.Ordinal);
                while (data[index] != (byte)'e')
                {
                    var key = Encoding.UTF8.GetString(ReadBytes(data, ref index));
                    dictionary[key] = Read(data, ref index, depth + 1);
                }

                index++;
                return BValue.FromDictionary(dictionary, start, index - start);

            case >= (byte)'0' and <= (byte)'9':
                var bytes = ReadBytes(data, ref index);
                return BValue.FromBytes(bytes, start, index - start);

            default:
                throw new InvalidDataException($"Unexpected byte 0x{data[index]:X2} in bencoded data.");
        }
    }

    private static byte[] ReadBytes(byte[] data, ref int index)
    {
        var colon = Array.IndexOf(data, (byte)':', index);
        if (colon < 0 || !int.TryParse(Encoding.ASCII.GetString(data, index, colon - index), out var length)
            || length < 0 || colon + 1 + length > data.Length)
        {
            throw new InvalidDataException("Bad bencoded string.");
        }

        var bytes = data.AsSpan(colon + 1, length).ToArray();
        index = colon + 1 + length;
        return bytes;
    }
}

using System.Text;

namespace Nexus.Core.Games;

/// <summary>
/// Reads app types ("Game", "Application", "Tool"…) from Steam's binary appcache\appinfo.vdf,
/// so programs like Blender or Wallpaper Engine stay out of the game library.
/// Supports format versions 28 and 29 (string table); anything else yields no types.
/// </summary>
public static class SteamAppInfo
{
    private const uint Version28 = 0x07564428;
    private const uint Version29 = 0x07564429;

    // infoState, lastUpdated, picsToken, SHA-1 of the text data, changeNumber, SHA-1 of the binary data.
    private const int EntryHeaderSize = 4 + 4 + 8 + 20 + 4 + 20;

    private const byte MapType = 0x00;
    private const byte StringType = 0x01;
    private const byte Int32Type = 0x02;
    private const byte FloatType = 0x03;
    private const byte PointerType = 0x04;
    private const byte WideStringType = 0x05;
    private const byte ColorType = 0x06;
    private const byte UInt64Type = 0x07;
    private const byte EndType = 0x08;
    private const byte Int64Type = 0x0A;

    /// <summary>Steam types that are not something to play.</summary>
    public static bool IsNotAGame(string type) =>
        type.Equals("Application", StringComparison.OrdinalIgnoreCase)
        || type.Equals("Tool", StringComparison.OrdinalIgnoreCase)
        || type.Equals("Music", StringComparison.OrdinalIgnoreCase)
        || type.Equals("Video", StringComparison.OrdinalIgnoreCase)
        || type.Equals("Config", StringComparison.OrdinalIgnoreCase)
        || type.Equals("Hardware", StringComparison.OrdinalIgnoreCase)
        || type.Equals("Media", StringComparison.OrdinalIgnoreCase);

    /// <summary>The "common/type" of the given apps; apps it cannot read are simply missing.</summary>
    public static IReadOnlyDictionary<string, string> ReadTypes(string path, IEnumerable<string> appIds)
    {
        var wanted = appIds.Select(id => uint.TryParse(id, out var value) ? value : 0).Where(value => value != 0).ToHashSet();
        var types = new Dictionary<string, string>();
        if (wanted.Count == 0 || !File.Exists(path))
        {
            return types;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            var version = reader.ReadUInt32();
            reader.ReadUInt32(); // universe
            if (version is not (Version28 or Version29))
            {
                return types;
            }

            string[]? strings = null;
            if (version == Version29)
            {
                var tableOffset = reader.ReadInt64();
                var entries = stream.Position;
                stream.Position = tableOffset;
                strings = ReadStringTable(reader);
                stream.Position = entries;
            }

            while (stream.Position + 8 <= stream.Length && types.Count < wanted.Count)
            {
                var appId = reader.ReadUInt32();
                if (appId == 0)
                {
                    break;
                }

                var next = stream.Position + 4 + reader.ReadUInt32();
                if (wanted.Contains(appId))
                {
                    stream.Position += EntryHeaderSize;
                    if (FindCommonType(reader, strings, next) is { } type)
                    {
                        types[appId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = type;
                    }
                }

                stream.Position = next;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException
            or ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException)
        {
            // A format change on Steam's side: keep what was read, the library just shows every app.
        }

        return types;
    }

    private static string[] ReadStringTable(BinaryReader reader)
    {
        var count = reader.ReadUInt32();
        var strings = new string[count];
        for (var index = 0; index < count; index++)
        {
            strings[index] = ReadString(reader);
        }

        return strings;
    }

    /// <summary>Walks the binary KeyValues of one app looking for appinfo/common/type.</summary>
    private static string? FindCommonType(BinaryReader reader, string[]? strings, long end)
    {
        var path = new Stack<string>();
        while (reader.BaseStream.Position < end)
        {
            var type = reader.ReadByte();
            if (type == EndType)
            {
                if (path.Count == 0)
                {
                    return null;
                }

                path.Pop();
                continue;
            }

            var key = strings is null ? ReadString(reader) : strings[reader.ReadInt32()];
            switch (type)
            {
                case MapType:
                    path.Push(key);
                    break;
                case StringType:
                    var value = ReadString(reader);
                    if (path.Count > 0 && path.Peek().Equals("common", StringComparison.OrdinalIgnoreCase)
                        && key.Equals("type", StringComparison.OrdinalIgnoreCase))
                    {
                        return value;
                    }

                    break;
                case WideStringType:
                    while (reader.ReadUInt16() != 0)
                    {
                    }

                    break;
                case Int32Type or FloatType or PointerType or ColorType:
                    reader.BaseStream.Position += 4;
                    break;
                case UInt64Type or Int64Type:
                    reader.BaseStream.Position += 8;
                    break;
                default:
                    throw new InvalidDataException($"Unknown KeyValues type 0x{type:X2}.");
            }
        }

        return null;
    }

    private static string ReadString(BinaryReader reader)
    {
        var bytes = new List<byte>();
        for (var value = reader.ReadByte(); value != 0; value = reader.ReadByte())
        {
            bytes.Add(value);
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}

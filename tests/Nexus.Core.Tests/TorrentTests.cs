using System.Security.Cryptography;
using System.Text;
using Nexus.Core.Torrents;

namespace Nexus.Core.Tests;

public sealed class TorrentTests : IDisposable
{
    private readonly TempFolder _temp = new();

    [Fact]
    public void Bencode_DecodesNestedValues()
    {
        var value = Bencode.Decode(Encoding.ASCII.GetBytes("d3:cow3:moo3:numi-42e4:spaml1:a1:bee"));

        Assert.Equal("moo", value["cow"]?.Text);
        Assert.Equal(-42, value["num"]?.Integer);
        Assert.Equal(["a", "b"], value["spam"]!.List!.Select(item => item.Text));
        Assert.Null(value["missing"]);
    }

    [Theory]
    [InlineData("d3:cow")]
    [InlineData("i12")]
    [InlineData("5:ab")]
    [InlineData("x")]
    [InlineData("iabce")]
    public void Bencode_RejectsBrokenData(string data)
    {
        Assert.Throws<InvalidDataException>(() => Bencode.Decode(Encoding.ASCII.GetBytes(data)));
        Assert.Null(Bencode.TryDecode(Encoding.ASCII.GetBytes(data)));
    }

    [Fact]
    public void Bencode_RejectsDeepNesting()
    {
        var data = Encoding.ASCII.GetBytes(new string('l', 200) + new string('e', 200));

        Assert.Throws<InvalidDataException>(() => Bencode.Decode(data));
    }

    [Fact]
    public void TorrentInfo_HashesTheInfoDictionaryAndSumsFiles()
    {
        var info = new Dictionary<string, object>
        {
            ["name"] = "Альбом",
            ["piece length"] = 16384L,
            ["pieces"] = new byte[40],
            ["files"] = new object[]
            {
                new Dictionary<string, object> { ["length"] = 100L, ["path"] = new object[] { "01.flac" } },
                new Dictionary<string, object> { ["length"] = 50L, ["path"] = new object[] { "cover.jpg" } }
            }
        };
        var path = Torrent("album.torrent", info);

        var read = TorrentLibrary.ReadInfo(path);

        Assert.NotNull(read);
        Assert.Equal("Альбом", read.Name);
        Assert.Equal(150, read.Size);
        Assert.Equal(2, read.FileCount);
        Assert.Equal(2, read.PieceCount);
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(Encode(info))), read.InfoHash);
    }

    [Fact]
    public void UTorrentResume_ReadsProgressFromPieceBitsAndSkipsServiceKeys()
    {
        var folder = _temp.Folder("BitTorrent");
        Torrent(@"BitTorrent\ubuntu.torrent", SingleFile("ubuntu.iso", 4000, pieces: 4));
        var hash = Enumerable.Range(1, 20).Select(value => (byte)value).ToArray();
        WriteBencoded(@"BitTorrent\resume.dat", new Dictionary<string, object>
        {
            [".fileguard"] = "0123456789",
            ["rec"] = new Dictionary<string, object> { ["x"] = 1L },
            ["ubuntu.torrent"] = new Dictionary<string, object>
            {
                ["path"] = @"C:\Downloads\ubuntu.iso",
                ["have"] = new byte[] { 0b1100_0000 },
                ["added_on"] = 1_700_000_000L,
                ["completed_on"] = 0L,
                ["info"] = hash
            },
            ["movie.torrent"] = new Dictionary<string, object>
            {
                ["caption"] = "Мой фильм",
                ["path"] = @"C:\Downloads\Movie",
                ["completed_on"] = 1_700_001_000L
            }
        });

        var downloads = TorrentLibrary.ReadUTorrentFamily(folder, "BitTorrent");

        Assert.Equal(2, downloads.Count);
        var ubuntu = downloads.Single(download => download.Name == "ubuntu.iso");
        Assert.Equal(0.5, ubuntu.Progress);
        Assert.False(ubuntu.IsComplete);
        Assert.Equal(4000, ubuntu.Size);
        Assert.Equal(@"C:\Downloads\ubuntu.iso", ubuntu.ContentPath);
        Assert.Equal(Convert.ToHexStringLower(hash), ubuntu.InfoHash);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), ubuntu.Added);
        var movie = downloads.Single(download => download.Name == "Мой фильм");
        Assert.True(movie.IsComplete);
        Assert.Null(movie.Size);
    }

    [Fact]
    public void UTorrentResume_MissingOrBrokenFileGivesNothing()
    {
        Assert.Empty(TorrentLibrary.ReadUTorrentFamily(_temp.Folder("nothing"), "µTorrent"));
        _temp.File(@"broken\resume.dat", "d10:.fileguard");
        Assert.Empty(TorrentLibrary.ReadUTorrentFamily(Path.Combine(_temp.Path, "broken"), "µTorrent"));
    }

    [Fact]
    public void QBittorrent_ReadsFastresumeWithTheTorrentNextToIt()
    {
        var folder = _temp.Folder("BT_backup");
        var info = SingleFile("Album", 2000, pieces: 4);
        var hash = Convert.ToHexStringLower(SHA1.HashData(Encode(info)));
        Torrent($@"BT_backup\{hash}.torrent", info);
        WriteBencoded($@"BT_backup\{hash}.fastresume", new Dictionary<string, object>
        {
            ["save_path"] = @"D:\Torrents",
            ["qBt-name"] = string.Empty,
            ["pieces"] = new byte[] { 1, 3, 0, 0 },
            ["added_time"] = 1_700_000_000L,
            ["completed_time"] = 0L
        });
        _temp.File(@"BT_backup\junk.fastresume", "not bencoded");

        var download = Assert.Single(TorrentLibrary.ReadQBittorrent(folder));

        Assert.Equal("Album", download.Name);
        Assert.Equal("qBittorrent", download.Client);
        Assert.Equal(@"D:\Torrents\Album", download.ContentPath);
        Assert.Equal(0.5, download.Progress);
        Assert.Equal(hash, download.InfoHash);
    }

    [Fact]
    public void Transmission_ReadsResumeWithTheTorrentNextToIt()
    {
        var folder = _temp.Folder("transmission");
        var info = SingleFile("Film.mkv", 4000, pieces: 4);
        var hash = HashOf(info);
        Torrent($@"transmission\torrents\{hash}.torrent", info);
        WriteBencoded($@"transmission\resume\{hash}.resume", new Dictionary<string, object>
        {
            ["destination"] = @"E:\Video",
            ["added-date"] = 1_700_000_000L,
            ["done-date"] = 0L,
            ["progress"] = new Dictionary<string, object> { ["pieces"] = new byte[] { 0b1110_0000 }, ["blocks"] = "none" }
        });
        WriteBencoded(@"transmission\resume\album.resume", new Dictionary<string, object>
        {
            ["name"] = "Old album",
            ["destination"] = @"E:\Music",
            ["done-date"] = 1_700_000_500L,
            ["progress"] = new Dictionary<string, object> { ["have"] = "all" }
        });

        var downloads = TorrentLibrary.ReadTransmission(folder);

        Assert.Equal(2, downloads.Count);
        var film = downloads.Single(download => download.Name == "Film.mkv");
        Assert.Equal(0.75, film.Progress);
        Assert.Equal(@"E:\Video\Film.mkv", film.ContentPath);
        Assert.Equal(hash, film.InfoHash);
        var album = downloads.Single(download => download.Name == "Old album");
        Assert.True(album.IsComplete);
        Assert.Equal(@"E:\Music\Old album", album.ContentPath);
    }

    [Fact]
    public void Deluge_ReadsNestedLibtorrentResumeData()
    {
        var state = _temp.Folder(@"deluge\state");
        var info = SingleFile("Distro.iso", 3000, pieces: 2);
        var hash = HashOf(info);
        Torrent($@"deluge\state\{hash}.torrent", info);
        WriteBencoded(@"deluge\state\torrents.fastresume", new Dictionary<string, object>
        {
            [hash] = Encode(new Dictionary<string, object>
            {
                ["save_path"] = @"C:\Torrents",
                ["pieces"] = new byte[] { 1, 1 },
                ["added_time"] = 1_700_000_000L,
                ["completed_time"] = 1_700_000_100L
            }),
            ["not-a-hash"] = Encode(new Dictionary<string, object> { ["save_path"] = "X" })
        });

        var download = Assert.Single(TorrentLibrary.ReadDeluge(state));

        Assert.Equal("Distro.iso", download.Name);
        Assert.Equal("Deluge", download.Client);
        Assert.True(download.IsComplete);
        Assert.Equal(@"C:\Torrents\Distro.iso", download.ContentPath);
        Assert.Equal(hash, download.InfoHash);
    }

    [Fact]
    public void LoadDownloads_CombinesEveryClientFromItsOwnFolder()
    {
        var appData = _temp.Folder("Roaming");
        var localAppData = _temp.Folder("Local");
        WriteBencoded(@"Roaming\BitTorrent\resume.dat", new Dictionary<string, object>
        {
            ["a.torrent"] = new Dictionary<string, object> { ["caption"] = "From BitTorrent", ["completed_on"] = 1L }
        });
        WriteBencoded(@"Local\qBittorrent\BT_backup\b.fastresume", new Dictionary<string, object> { ["qBt-name"] = "From qBittorrent" });
        WriteBencoded(@"Local\transmission\resume\c.resume", new Dictionary<string, object> { ["name"] = "From Transmission" });
        var deluge = SingleFile("From Deluge", 10, pieces: 1);
        var hash = HashOf(deluge);
        Torrent($@"Roaming\deluge\state\{hash}.torrent", deluge);
        WriteBencoded(@"Roaming\deluge\state\torrents.fastresume", new Dictionary<string, object>
        {
            [hash] = Encode(new Dictionary<string, object> { ["save_path"] = @"C:\T" })
        });

        var downloads = TorrentLibrary.LoadDownloads(appData, localAppData);

        Assert.Equal(["BitTorrent", "Deluge", "Transmission", "qBittorrent"], downloads.Select(download => download.Client).Order(StringComparer.Ordinal));
        Assert.All(downloads, download => Assert.Equal($"From {download.Client}", download.Name));
    }

    [Fact]
    public void FindFiles_LooksOneLevelDeepAndKeepsBrokenFiles()
    {
        var downloads = _temp.Folder("Downloads");
        Torrent(@"Downloads\a.torrent", SingleFile("Game", 300, pieces: 1));
        Torrent(@"Downloads\sub\b.torrent", SingleFile("Book.pdf", 20, pieces: 1));
        Torrent(@"Downloads\sub\deeper\c.torrent", SingleFile("Hidden", 1, pieces: 1));
        _temp.File(@"Downloads\broken.torrent", "garbage");

        var files = TorrentLibrary.FindFiles([downloads, Path.Combine(_temp.Path, "missing")]);

        Assert.Equal(["Book.pdf", "Game", "broken"], files.Select(file => file.Name).Order(StringComparer.Ordinal));
        Assert.Equal(300, files.Single(file => file.Name == "Game").Size);
        Assert.Null(files.Single(file => file.Name == "broken").InfoHash);
    }

    public void Dispose() => _temp.Dispose();

    private static Dictionary<string, object> SingleFile(string name, long length, int pieces) => new()
    {
        ["name"] = name,
        ["length"] = length,
        ["piece length"] = 1024L,
        ["pieces"] = new byte[20 * pieces]
    };

    private static string HashOf(Dictionary<string, object> info) => Convert.ToHexStringLower(SHA1.HashData(Encode(info)));

    private string Torrent(string relativePath, Dictionary<string, object> info) =>
        WriteBencoded(relativePath, new Dictionary<string, object> { ["announce"] = "http://tracker.example/announce", ["info"] = info });

    private string WriteBencoded(string relativePath, object value)
    {
        var path = Path.Combine(_temp.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encode(value));
        return path;
    }

    private static byte[] Encode(object value)
    {
        using var stream = new MemoryStream();
        Write(stream, value);
        return stream.ToArray();
    }

    private static void Write(MemoryStream stream, object value)
    {
        switch (value)
        {
            case long number:
                stream.Write(Encoding.ASCII.GetBytes($"i{number}e"));
                break;
            case string text:
                Write(stream, Encoding.UTF8.GetBytes(text));
                break;
            case byte[] bytes:
                stream.Write(Encoding.ASCII.GetBytes($"{bytes.Length}:"));
                stream.Write(bytes);
                break;
            case Dictionary<string, object> dictionary:
                stream.WriteByte((byte)'d');
                foreach (var (key, item) in dictionary.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    Write(stream, key);
                    Write(stream, item);
                }

                stream.WriteByte((byte)'e');
                break;
            case object[] list:
                stream.WriteByte((byte)'l');
                foreach (var item in list)
                {
                    Write(stream, item);
                }

                stream.WriteByte((byte)'e');
                break;
            default:
                throw new ArgumentException($"Cannot bencode {value.GetType()}.", nameof(value));
        }
    }
}

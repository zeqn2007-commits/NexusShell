namespace Nexus.Core.Models;

public sealed record GameEntry(
    string Name,
    string InstallPath,
    string Source,
    string? LaunchTarget,
    string? AppId = null,
    string? ArtworkPath = null,
    string? RelatedTorrentPath = null,
    string? DetectionDetail = null,
    bool CanDeleteFiles = false)
{
    public bool CanLaunch => !string.IsNullOrWhiteSpace(LaunchTarget);

    public bool IsLocalInstall => Source is "Local" or "Torrent";

    public string SourceLabel => Source switch
    {
        "Steam" => "Steam",
        "Epic Games" => "Epic Games",
        "Xbox" => "Xbox",
        "Local" => "Локальная игра",
        "Torrent" => "Торрент / локальная игра",
        _ => Source
    };

    public string ActionLabel => CanLaunch ? "Запустить" : "Открыть папку";

    public string LibraryDetail => !string.IsNullOrWhiteSpace(DetectionDetail)
        ? DetectionDetail
        : RelatedTorrentPath is not null
            ? "Найдена по торренту"
            : CanLaunch
                ? "Готова к запуску"
                : "Доступны файлы игры";

    public double ArtworkOpacity => string.IsNullOrWhiteSpace(ArtworkPath)
        ? 0
        : 1;

    public string? ArtworkSource => ToFileUri(ArtworkPath);

    public double FallbackOpacity => ArtworkOpacity == 0 ? 1 : 0;

    public string Glyph => Source switch
    {
        "Steam" => "\uE7FC",
        "Epic Games" => "\uE7FC",
        "Xbox" => "\uE7FC",
        _ => "\uE7FC"
    };

    private static string? ToFileUri(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return new Uri(Path.GetFullPath(path)).AbsoluteUri;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or UriFormatException)
        {
            return null;
        }
    }
}

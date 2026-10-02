namespace Nexus.App.Services;

internal static class CrashLog
{
    private const long MaxLogSizeBytes = 1024 * 1024;

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Nexus Shell",
        "Logs");

    public static void Write(Exception exception)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = Path.Combine(Directory, "crash.log");
            if (File.Exists(path) && new FileInfo(path).Length > MaxLogSizeBytes)
            {
                File.Move(path, path + ".old", overwrite: true);
            }

            File.AppendAllText(path, $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception logFailure) when (logFailure is IOException or UnauthorizedAccessException)
        {
            // Logging must never take the app down with it.
        }
    }
}

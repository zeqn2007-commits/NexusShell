using System.Text.Json;

namespace Nexus.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON in
/// %LOCALAPPDATA%\Nexus Shell\settings.json. Writes go to a temporary file
/// first and replace the old file atomically, so a crash never leaves half a file.
/// </summary>
public sealed class SettingsStore
{
    private readonly Lock _gate = new();
    private readonly string _path;

    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(DataDirectory, "settings.json");
        Current = Load(_path);
    }

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Nexus Shell");

    public AppSettings Current { get; }

    public event EventHandler? Changed;

    /// <summary>Applies a change and persists it. Returns false when saving failed (the change stays in memory).</summary>
    public bool Update(Action<AppSettings> change)
    {
        bool saved;
        lock (_gate)
        {
            change(Current);
            saved = TrySave();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    private bool TrySave()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings));
            File.Move(temporary, _path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged settings file must not stop Nexus from starting; keep a copy for diagnosis.
            try
            {
                File.Copy(path, path + ".broken", overwrite: true);
            }
            catch (Exception copyFailure) when (copyFailure is IOException or UnauthorizedAccessException)
            {
            }
        }

        return new AppSettings();
    }
}

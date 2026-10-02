using Nexus.App.Models;

namespace Nexus.App.Demo;

/// <summary>
/// Sample content for the design review build. Every list here is replaced by
/// real Core services in the next phase; nothing in this class touches user data
/// except reading Steam cover images when they exist.
/// </summary>
internal static class DemoData
{
    private static readonly string SteamCache = @"C:\Program Files (x86)\Steam\appcache\librarycache";
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    public static string DocumentsPath { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public static IReadOnlyList<FileItem> QuickAccess { get; } =
    [
        Folder("Рабочий стол", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
        Folder("Загрузки", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")),
        Folder("Документы", DocumentsPath),
        Folder("Изображения", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
        Folder("Музыка", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
        Folder("Видео", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
        Folder("Проекты", Path.Combine(DocumentsPath, "Проекты")),
        Folder("Учёба", Path.Combine(DocumentsPath, "Учёба"))
    ];

    public static IReadOnlyList<FileItem> Recent { get; } =
    [
        File("Отчёт по практике ПМ.04.docx", "Документы › Учёба", 2_184_000, Now.AddMinutes(-25)),
        File("Бюджет на октябрь.xlsx", "Документы", 86_200, Now.AddHours(-3)),
        File("Презентация проекта.pptx", "Документы › Проекты", 12_900_000, Now.AddHours(-5)),
        File("Скриншот 2026-10-01 223015.png", "Изображения › Снимки экрана", 1_350_000, Now.AddDays(-1).AddHours(-1)),
        File("Договор аренды.pdf", "Загрузки", 640_000, Now.AddDays(-1).AddHours(-6)),
        File("README.md", "Документы › Проекты › nexus", 4_100, Now.AddDays(-2)),
        File("Плейлист для работы.mp3", "Музыка", 8_400_000, Now.AddDays(-3)),
        File("blender-5.2-windows-x64.zip", "Загрузки", 412_000_000, Now.AddDays(-4))
    ];

    public static IReadOnlyList<FileItem> DocumentsFolder { get; } =
    [
        Folder("Проекты", Path.Combine(DocumentsPath, "Проекты"), Now.AddHours(-5)),
        Folder("Учёба", Path.Combine(DocumentsPath, "Учёба"), Now.AddMinutes(-25)),
        Folder("Работа", Path.Combine(DocumentsPath, "Работа"), Now.AddDays(-2)),
        Folder("Сканы", Path.Combine(DocumentsPath, "Сканы"), Now.AddDays(-9)),
        Folder("Архив", Path.Combine(DocumentsPath, "Архив"), Now.AddDays(-40)),
        File("Отчёт по практике ПМ.04.docx", "Документы", 2_184_000, Now.AddMinutes(-25)),
        File("Бюджет на октябрь.xlsx", "Документы", 86_200, Now.AddHours(-3)),
        File("Презентация проекта.pptx", "Документы", 12_900_000, Now.AddHours(-5)),
        File("Договор аренды.pdf", "Документы", 640_000, Now.AddDays(-1)),
        File("Заметки.txt", "Документы", 2_300, Now.AddDays(-2)),
        File("Фото на паспорт.jpg", "Документы", 1_870_000, Now.AddDays(-6)),
        File("Резюме — Власов Е.docx", "Документы", 96_400, Now.AddDays(-12)),
        File("Курсовая работа (финал).pdf", "Документы", 3_420_000, Now.AddDays(-18)),
        File("Расписание.xlsx", "Документы", 42_100, Now.AddDays(-21)),
        File("backup-2026-09.zip", "Документы", 1_240_000_000, Now.AddDays(-30)),
        File("setup-helper.exe", "Документы", 5_600_000, Now.AddDays(-44)),
        File("config.json", "Документы", 1_200, Now.AddDays(-51))
    ];

    public static IReadOnlyList<GameItem> Games { get; } =
    [
        Steam("Terraria", 105600, 803_964_902, Now.AddHours(-20)),
        Steam("Portal 2", 620, 13_504_412_280, Now.AddDays(-1)),
        Steam("BioShock Remastered", 409710, 22_182_252_597, null),
        new() { Name = "Hollow Knight", Source = GameSource.Local, InstallPath = @"D:\Games\Hollow Knight", SizeBytes = 9_100_000_000, LastPlayed = Now.AddDays(-6), PaletteFrom = "#FF2B3A55", PaletteTo = "#FF0E1420" },
        new() { Name = "Hades II", Source = GameSource.Epic, InstallPath = @"D:\Epic Games\HadesII", SizeBytes = 11_300_000_000, LastPlayed = Now.AddDays(-9), PaletteFrom = "#FF7A2E3B", PaletteTo = "#FF2A0F18" },
        new() { Name = "Forza Horizon 5", Source = GameSource.Xbox, InstallPath = @"C:\XboxGames\Forza Horizon 5", SizeBytes = 118_000_000_000, PaletteFrom = "#FF1F6B7A", PaletteTo = "#FF0B2730" },
        new() { Name = "Stardew Valley", Source = GameSource.Local, InstallPath = @"D:\Games\Stardew Valley", SizeBytes = 620_000_000, LastPlayed = Now.AddDays(-14), PaletteFrom = "#FF3F7A3A", PaletteTo = "#FF15301A" },
        new() { Name = "Celeste", Source = GameSource.Local, InstallPath = @"D:\Games\Celeste", SizeBytes = 1_200_000_000, PaletteFrom = "#FF6A3F8F", PaletteTo = "#FF231536" }
    ];

    public static IReadOnlyList<AiModelItem> Models { get; } =
    [
        new() { Name = "qwen3:32b", Runtime = "Ollama", Parameters = "32B", Quantization = "Q4_K_M", SizeBytes = 20_200_000_000, IsLoaded = true },
        new() { Name = "llama3.3:8b", Runtime = "Ollama", Parameters = "8B", Quantization = "Q5_K_M", SizeBytes = 5_700_000_000 },
        new() { Name = "gemma3:12b", Runtime = "Ollama", Parameters = "12B", Quantization = "Q4_0", SizeBytes = 8_100_000_000 },
        new() { Name = "deepseek-r1-distill-qwen-14b", Runtime = "LM Studio", Parameters = "14B", Quantization = "Q6_K", SizeBytes = 12_100_000_000 }
    ];

    public static IReadOnlyList<AiProjectItem> AiProjects { get; } =
    [
        new() { Name = "nexus-shell", Path = @"C:\Users\zeqn2\Desktop\NexusShell", Kind = "Проект с агентом", Modified = Now.AddMinutes(-5), Signals = ["CLAUDE.md", ".claude", "AI-код"] },
        new() { Name = "gitbench", Path = @"C:\Users\zeqn2\Desktop\gitbench", Kind = "Проект с агентом", Modified = Now.AddDays(-1), Signals = ["CLAUDE.md", "MCP"] },
        new() { Name = "telegram-assistant", Path = @"D:\Dev\telegram-assistant", Kind = "AI-проект", Modified = Now.AddDays(-4), Signals = ["openai", "langchain"] },
        new() { Name = "ComfyUI", Path = @"D:\AI\ComfyUI", Kind = "AI-инструмент", Modified = Now.AddDays(-11), Signals = ["модели", "workflows"] }
    ];

    public static IReadOnlyList<McpServerItem> McpServers { get; } =
    [
        new() { Name = "filesystem", Client = "Claude", Command = "npx @modelcontextprotocol/server-filesystem" },
        new() { Name = "github", Client = "Codex", Command = "github-mcp-server stdio" },
        new() { Name = "blender", Client = "Claude", Command = "blender-mcp" }
    ];

    public static IReadOnlyList<DriveItem> Drives()
    {
        var drives = new List<DriveItem>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                {
                    continue;
                }

                var letter = drive.Name.TrimEnd('\\');
                drives.Add(new DriveItem
                {
                    Name = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? $"Локальный диск ({letter})"
                        : $"{drive.VolumeLabel} ({letter})",
                    RootPath = drive.RootDirectory.FullName,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace
                });
            }
            catch (IOException)
            {
                // A drive that disappears while being queried is simply skipped.
            }
        }

        return drives;
    }

    private static FileItem Folder(string name, string path, DateTimeOffset? modified = null) => new()
    {
        Name = name,
        Path = path,
        Kind = FileKind.Folder,
        Modified = modified ?? Now.AddDays(-1),
        Created = (modified ?? Now).AddMonths(-7),
        Location = "Хранится локально"
    };

    private static FileItem File(string name, string location, long size, DateTimeOffset modified) => new()
    {
        Name = name,
        Path = Path.Combine(DocumentsPath, name),
        Kind = FileKinds.FromExtension(Path.GetExtension(name)),
        SizeBytes = size,
        Modified = modified,
        Created = modified.AddDays(-3),
        Location = location
    };

    private static GameItem Steam(string name, int appId, long size, DateTimeOffset? lastPlayed) => new()
    {
        Name = name,
        Source = GameSource.Steam,
        InstallPath = $@"C:\Program Files (x86)\Steam\steamapps\common\{name}",
        CoverPath = Path.Combine(SteamCache, appId.ToString(), "library_600x900.jpg"),
        HeroPath = Path.Combine(SteamCache, appId.ToString(), "library_hero.jpg"),
        SizeBytes = size,
        LastPlayed = lastPlayed
    };
}

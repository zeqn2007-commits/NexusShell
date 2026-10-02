using System.ComponentModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Nexus.App.Models;
using Nexus.App.Services;
using Nexus.App.Shell;
using Nexus.Core.Ai;
using Nexus.Core.IO;
using Nexus.Core.Operations;
using Nexus.Core.Shell;

namespace Nexus.App.ViewModels;

/// <summary>
/// "AI-центр": local models, skills, MCP servers, AI projects and — the smart section —
/// AI files that landed in Downloads, on the desktop or in Documents.
/// </summary>
public sealed partial class AiCenterViewModel(
    AiWorkspace workspace,
    FileOperationService operations,
    UndoHistory undo,
    ShellViewModel shell,
    WindowContext window,
    DialogService dialogs) : ObservableObject
{
    private const int CollapsedSkillCount = 9;
    private IReadOnlyList<AiSkillItem> _allSkills = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelsCount), nameof(ModelsCaption), nameof(HasModels))]
    public partial IReadOnlyList<AiModelItem> Models { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProjectsCount), nameof(ProjectsCaption), nameof(HasProjects))]
    public partial IReadOnlyList<AiProjectItem> Projects { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(McpCount), nameof(McpCaption), nameof(HasMcp))]
    public partial IReadOnlyList<McpServerItem> McpServers { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<AiSkillItem> Skills { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFiles), nameof(FilesCaption))]
    public partial IReadOnlyList<AiFileItem> Files { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsScanning { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SkillsToggleText))]
    public partial bool ShowAllSkills { get; set; }

    public string ModelsCount => Models.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public string ModelsCaption => Models.Count == 0
        ? "моделей"
        : $"{Formatting.Word(Models.Count, "модель", "модели", "моделей")} · {Formatting.Size(Models.Sum(model => model.Model.SizeBytes))}";

    public string ProjectsCount => Projects.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public string McpCount => McpServers.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public string SkillsCount => _allSkills.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public string SkillsCaption => Formatting.Word(_allSkills.Count, "скилл", "скилла", "скиллов");

    public string ProjectsCaption => Formatting.Word(Projects.Count, "AI-проект", "AI-проекта", "AI-проектов");

    public string McpCaption => Formatting.Word(McpServers.Count, "MCP-сервер", "MCP-сервера", "MCP-серверов");

    public bool HasModels => Models.Count > 0;

    public bool HasProjects => Projects.Count > 0;

    public bool HasMcp => McpServers.Count > 0;

    public bool HasSkills => _allSkills.Count > 0;

    public bool HasFiles => Files.Count > 0;

    public bool CanToggleSkills => _allSkills.Count > CollapsedSkillCount;

    public string SkillsToggleText => ShowAllSkills ? "Свернуть" : $"Показать все ({_allSkills.Count})";

    public string FilesCaption => $"Скиллы, модели, MCP-конфиги и промпты в «Загрузках», на рабочем столе и в «Документах»"
        + (Files.Count > 0 ? $" · {Formatting.Items(Files.Count)}" : string.Empty);

    public IReadOnlyList<string> ExtraFolders => workspace.ExtraFolders;

    public string StatusText => workspace.LatestAt is { } at
        ? $"Сканирование: {Formatting.RelativeDate(at, DateTimeOffset.Now).ToLowerInvariant()}"
        : string.Empty;

    public async Task LoadAsync(bool force)
    {
        IsScanning = true;
        try
        {
            Apply(force ? await workspace.ScanAsync() : await workspace.GetAsync());
        }
        finally
        {
            IsScanning = false;
        }
    }

    partial void OnShowAllSkillsChanged(bool value) => UpdateVisibleSkills();

    public void Open(AiFileItem item)
    {
        if (item.File.IsFolder)
        {
            shell.Navigate(NavLocation.ForFolder(item.File.Path));
            return;
        }

        try
        {
            ShellLauncher.Open(item.File.Path);
        }
        catch (Win32Exception exception)
        {
            shell.NotifyError($"Windows не смогла открыть «{item.Name}»: {exception.Message}");
        }
    }

    public void ShowInFolder(AiFileItem item)
    {
        if (PathHelper.GetParent(item.File.Path) is { } folder)
        {
            shell.RequestSelection(item.File.Path);
            shell.Navigate(NavLocation.ForFolder(folder));
        }
    }

    /// <summary>Moves the item into Documents › AI › (Скиллы | Модели | MCP | Промпты), with undo.</summary>
    public async Task PutAwayAsync(AiFileItem item)
    {
        var destination = Path.Combine(KnownFolders.GetPath(KnownFolder.Documents), "AI", item.File.Kind switch
        {
            AiFileKind.Skill => "Скиллы",
            AiFileKind.Model => "Модели",
            AiFileKind.McpConfig => "MCP",
            _ => "Промпты"
        });
        Directory.CreateDirectory(destination);
        var started = DateTimeOffset.Now;
        var outcome = await operations.MoveAsync([item.File.Path], destination, window.Handle);
        if (outcome.CreatedPaths.Count > 0)
        {
            undo.Record(MovedAction.FromOutcome([item.File.Path], outcome.CreatedPaths, started));
            shell.Notify($"«{item.Name}» перенесён в «Документы › AI › {Path.GetFileName(destination)}».", InfoBarSeverity.Success,
                actionText: "Отменить", action: UndoAsync);
        }
        else if (outcome.Error is not null && !outcome.Cancelled)
        {
            shell.NotifyError(outcome.Error);
        }

        await LoadAsync(force: true);
    }

    /// <summary>Copies a downloaded skill (folder, .skill or .zip) into ~/.claude/skills, with undo.</summary>
    public async Task InstallSkillAsync(AiFileItem item)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var skills = SkillInstaller.ClaudeSkillsFolder(profile);
        if (!await dialogs.ConfirmAsync(
                "Установить скилл для Claude Code?",
                $"«{item.Name}» будет скопирован в {skills}. Claude Code подхватит его в следующем сеансе. Сам файл останется на месте.",
                "Установить"))
        {
            return;
        }

        try
        {
            var work = Path.Combine(Path.GetTempPath(), "Nexus Shell", "skill-install");
            Directory.CreateDirectory(work);
            var source = await Task.Run(() => SkillInstaller.PrepareSource(item.File.Path, work));
            Directory.CreateDirectory(skills);
            var started = DateTimeOffset.Now;
            var outcome = await operations.CopyAsync([source], skills, window.Handle);
            if (outcome.CreatedPaths.Count > 0)
            {
                undo.Record(new CopiedAction(outcome.CreatedPaths, started));
                shell.Notify($"Скилл «{item.Name}» установлен для Claude Code.", InfoBarSeverity.Success, actionText: "Отменить", action: UndoAsync);
            }
            else if (outcome.Error is not null && !outcome.Cancelled)
            {
                shell.NotifyError(outcome.Error);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            shell.NotifyError(exception.Message, "Скилл не установлен");
        }

        await LoadAsync(force: true);
    }

    public async Task<bool> AddFolderAsync(string folder)
    {
        if (!workspace.AddFolder(folder))
        {
            shell.Notify("Эта папка уже есть в поиске.");
            return false;
        }

        await LoadAsync(force: true);
        return true;
    }

    public async Task RemoveFolderAsync(string folder)
    {
        workspace.RemoveFolder(folder);
        await LoadAsync(force: true);
    }

    private async Task UndoAsync()
    {
        try
        {
            shell.Notify(await undo.UndoLastAsync(window.Handle));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            shell.NotifyError(exception.Message, "Не удалось отменить");
        }

        await LoadAsync(force: true);
    }

    private void Apply(AiSnapshot snapshot)
    {
        Models = snapshot.Models.Select(model => new AiModelItem(model)).ToArray();
        Projects = snapshot.Projects.Select(project => new AiProjectItem(project)).ToArray();
        McpServers = snapshot.McpServers.Select(server => new McpServerItem(server)).ToArray();
        Files = snapshot.Files.Select(file => new AiFileItem(file)).ToArray();
        _allSkills = snapshot.Skills.Select(skill => new AiSkillItem(skill)).ToArray();
        UpdateVisibleSkills();
        OnPropertyChanged(nameof(SkillsCount));
        OnPropertyChanged(nameof(SkillsCaption));
        OnPropertyChanged(nameof(HasSkills));
        OnPropertyChanged(nameof(CanToggleSkills));
        OnPropertyChanged(nameof(SkillsToggleText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ExtraFolders));
    }

    private void UpdateVisibleSkills() =>
        Skills = ShowAllSkills ? _allSkills : _allSkills.Take(CollapsedSkillCount).ToArray();
}

using Nexus.Core.Ai;
using Nexus.Core.IO;

namespace Nexus.App.Models;

public sealed class AiModelItem(AiModel model)
{
    public AiModel Model { get; } = model;

    public string Name => Model.Name;

    public string Details => string.Join(" · ", new[] { Model.Runtime, Model.Parameters, Model.Quantization, Formatting.Size(Model.SizeBytes) }
        .Where(part => !string.IsNullOrWhiteSpace(part)));

    public bool IsLoaded => Model.IsLoaded;

    public string Status => IsLoaded ? "Загружена" : "Готова";
}

public sealed class AiProjectItem(AiProject project)
{
    public AiProject Project { get; } = project;

    public string Name => Project.Name;

    public string Path => Project.Path;

    public string Kind => Project.Kind;

    public IReadOnlyList<string> Signals => Project.Signals;

    public string RelativeModified => Formatting.RelativeDate(Project.Modified, DateTimeOffset.Now);
}

public sealed class McpServerItem(McpServer server)
{
    public McpServer Server { get; } = server;

    public string Name => Server.Name;

    public string Client => Server.Client;

    public string Command => Server.Command;
}

public sealed class AiSkillItem(AiSkill skill)
{
    public AiSkill Skill { get; } = skill;

    public string Name => Skill.Name;

    public string Description => string.IsNullOrWhiteSpace(Skill.Description) ? "Без описания" : Skill.Description;

    public string Source => Skill.Source;
}

/// <summary>An item of the smart section: an AI file found in Downloads, on the desktop or in Documents.</summary>
public sealed class AiFileItem(AiFile file)
{
    public AiFile File { get; } = file;

    public string Name => File.Name;

    public string KindTitle => File.KindTitle;

    public bool IsSkill => File.Kind == AiFileKind.Skill;

    public string Glyph => File.Kind switch
    {
        AiFileKind.Skill => "",
        AiFileKind.Model => "",
        AiFileKind.McpConfig => "",
        _ => ""
    };

    /// <summary>"Загрузки › skills" — where it lies, in the words of the sidebar.</summary>
    public string Location => Formatting.Location(PathHelper.GetParent(File.Path) ?? File.Path);

    public string Details => File.IsFolder
        ? $"{KindTitle} · {Formatting.RelativeDate(File.Modified, DateTimeOffset.Now)}"
        : $"{KindTitle} · {Formatting.Size(File.Size)} · {Formatting.RelativeDate(File.Modified, DateTimeOffset.Now)}";
}

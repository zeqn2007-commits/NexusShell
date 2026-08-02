namespace Nexus.Core.Models;

public enum AiProjectKind
{
    Project,
    ModelWorkspace,
    AgentWorkspace,
    AiTool
}

public sealed record AiProjectEntry(
    string Name,
    string FullPath,
    AiProjectKind Kind,
    DateTimeOffset ModifiedAt,
    IReadOnlyList<string> DetectionSignals)
{
    public string DisplayKind => Kind switch
    {
        AiProjectKind.ModelWorkspace => "Модели и датасеты",
        AiProjectKind.AgentWorkspace => "Агенты и автоматизация",
        AiProjectKind.AiTool => "AI-инструмент",
        _ => "AI-проект"
    };
}

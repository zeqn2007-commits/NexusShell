using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.App.Demo;
using Nexus.App.Models;

namespace Nexus.App.ViewModels;

public sealed partial class AiCenterViewModel : ObservableObject
{
    public IReadOnlyList<AiModelItem> Models { get; } = DemoData.Models;

    public IReadOnlyList<AiProjectItem> Projects { get; } = DemoData.AiProjects;

    public IReadOnlyList<McpServerItem> McpServers { get; } = DemoData.McpServers;

    public string ModelsCount => Models.Count.ToString();

    public string ModelsSize => Formatting.Size(Models.Sum(model => model.SizeBytes));

    public string ProjectsCount => Projects.Count.ToString();

    public string McpCount => McpServers.Count.ToString();

    public string SkillsCount => "12";
}

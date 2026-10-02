using Nexus.App.Models;

namespace Nexus.App.Demo;

/// <summary>
/// Sample content for the AI center until phase 5 connects it to Ollama, LM Studio and MCP configs.
/// Nothing here touches user data.
/// </summary>
internal static class DemoData
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

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
}

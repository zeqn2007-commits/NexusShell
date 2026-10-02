namespace Nexus.App.Models;

public sealed class AiModelItem
{
    public required string Name { get; init; }

    public required string Runtime { get; init; }

    public long SizeBytes { get; init; }

    public string? Quantization { get; init; }

    public string? Parameters { get; init; }

    public bool IsLoaded { get; init; }

    public string Details => string.Join(" · ", new[] { Runtime, Parameters, Quantization, Formatting.Size(SizeBytes) }
        .Where(part => !string.IsNullOrWhiteSpace(part)));

    public string Status => IsLoaded ? "Загружена" : "Готова";
}

public sealed class AiProjectItem
{
    public required string Name { get; init; }

    public required string Path { get; init; }

    public required string Kind { get; init; }

    public DateTimeOffset Modified { get; init; }

    public IReadOnlyList<string> Signals { get; init; } = [];

    public string RelativeModified => Formatting.RelativeDate(Modified, DateTimeOffset.Now);
}

public sealed class McpServerItem
{
    public required string Name { get; init; }

    public required string Client { get; init; }

    public required string Command { get; init; }
}

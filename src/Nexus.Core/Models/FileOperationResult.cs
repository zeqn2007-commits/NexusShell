namespace Nexus.Core.Models;

public sealed record FileOperationResult(
    bool Success,
    string Message,
    IReadOnlyList<string> ResultPaths)
{
    public IReadOnlyList<string> SkippedPaths { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static FileOperationResult Completed(string message, params string[] paths) =>
        new(true, message, paths);

    public static FileOperationResult Failed(
        string message,
        params string[] recoverablePaths) =>
        new(false, message, recoverablePaths);
}

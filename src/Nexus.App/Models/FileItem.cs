namespace Nexus.App.Models;

public sealed class FileItem
{
    public required string Name { get; init; }

    public required string Path { get; init; }

    public required FileKind Kind { get; init; }

    public DateTimeOffset Modified { get; init; }

    public DateTimeOffset Created { get; init; }

    public long? SizeBytes { get; init; }

    public string? Location { get; init; }

    public string? ThumbnailPath { get; init; }

    public bool IsFolder => Kind is FileKind.Folder or FileKind.Drive;

    public string Extension => IsFolder ? string.Empty : System.IO.Path.GetExtension(Name);

    public string TypeName => FileKinds.TypeName(Kind, Extension);

    public string DisplaySize => IsFolder ? string.Empty : Formatting.Size(SizeBytes);

    public string DisplayModified => Formatting.DateTime(Modified);

    public string DisplayCreated => Formatting.DateTime(Created);

    public string RelativeModified => Formatting.RelativeDate(Modified, DateTimeOffset.Now);
}

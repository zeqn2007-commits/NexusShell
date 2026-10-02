using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Nexus.Core.Shell;

/// <summary>
/// The file type names Explorer shows ("Документ Microsoft Word", "Текстовый документ"),
/// taken from the registered file associations in the user's language. Cached per extension.
/// </summary>
public static class ShellTypeNames
{
    private const uint ShgfiTypeName = 0x000000400;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private const uint FileAttributeNormal = 0x80;
    private const uint FileAttributeDirectory = 0x10;
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Get(string extension, bool isDirectory)
    {
        if (isDirectory)
        {
            return Cache.GetOrAdd("<folder>", _ => Query("folder", FileAttributeDirectory) ?? "Папка с файлами");
        }

        var key = string.IsNullOrEmpty(extension) ? "<none>" : extension;
        return Cache.GetOrAdd(key, _ =>
        {
            var name = Query("file" + extension, FileAttributeNormal);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            return string.IsNullOrEmpty(extension) ? "Файл" : $"Файл \"{extension.TrimStart('.').ToUpperInvariant()}\"";
        });
    }

    private static string? Query(string name, uint attributes)
    {
        var info = new ShFileInfo();
        var result = SHGetFileInfoW(name, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiTypeName | ShgfiUseFileAttributes);
        return result == IntPtr.Zero || string.IsNullOrWhiteSpace(info.TypeName) ? null : info.TypeName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);
}

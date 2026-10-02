using System.ComponentModel;
using System.Runtime.InteropServices;
using Nexus.Core.Interop;

namespace Nexus.Core.IO;

/// <summary>
/// The shared folders of a network computer (<c>\\server</c>), as Explorer lists them when the
/// computer is opened. Administrative shares (C$, ADMIN$) and printers are left out.
/// </summary>
public static class NetworkShares
{
    private const int MaxPreferredLength = -1;
    private const uint ShareTypeMask = 0xFF;
    private const uint DiskShare = 0;
    private const uint SpecialShare = 0x80000000;

    /// <summary>Can take a while for a computer that does not answer; call it off the UI thread.</summary>
    public static IReadOnlyList<FileEntry> Read(string computer)
    {
        var server = computer.Trim('\\');
        var result = NativeMethods.NetShareEnum(server, 1, out var buffer, MaxPreferredLength, out var count, out _, IntPtr.Zero);
        try
        {
            if (result != 0)
            {
                throw new IOException($"Не удалось открыть «{server}»: {new Win32Exception(result).Message}");
            }

            var entries = new List<FileEntry>(count);
            var size = Marshal.SizeOf<ShareInfo1>();
            for (var index = 0; index < count; index++)
            {
                var share = Marshal.PtrToStructure<ShareInfo1>(buffer + (index * size));
                if ((share.Type & ShareTypeMask) == DiskShare && (share.Type & SpecialShare) == 0 && !share.NetName.EndsWith('$'))
                {
                    entries.Add(new FileEntry(share.NetName, $@"\\{server}\{share.NetName}", true, 0, default, default, FileAttributes.Directory));
                }
            }

            return entries;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                NativeMethods.NetApiBufferFree(buffer);
            }
        }
    }

    public static Task<IReadOnlyList<FileEntry>> ReadAsync(string computer, CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(computer), cancellationToken);
}

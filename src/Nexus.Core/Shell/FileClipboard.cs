using System.Runtime.InteropServices;
using Nexus.Core.Interop;

namespace Nexus.Core.Shell;

public enum ClipboardEffect
{
    Copy = 1,
    Move = 2
}

public sealed record ClipboardFiles(IReadOnlyList<string> Paths, ClipboardEffect Effect);

/// <summary>
/// The classic CF_HDROP clipboard used by File Explorer, including the
/// "Preferred DropEffect" flag, so cut/copy/paste works both ways with Explorer.
/// </summary>
public static class FileClipboard
{
    private const uint CfHdrop = 15;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;
    private static readonly uint PreferredDropEffectFormat = NativeMethods.RegisterClipboardFormatW("Preferred DropEffect");

    public static bool HasFiles()
    {
        try
        {
            return NativeMethods.IsClipboardFormatAvailable(CfHdrop);
        }
        catch (Exception exception) when (exception is COMException or ExternalException)
        {
            return false;
        }
    }

    public static bool SetFiles(IReadOnlyList<string> paths, ClipboardEffect effect, IntPtr owner)
    {
        if (paths.Count == 0 || !TryOpen(owner))
        {
            return false;
        }

        IntPtr drop = IntPtr.Zero;
        IntPtr effectMemory = IntPtr.Zero;
        try
        {
            NativeMethods.EmptyClipboard();
            drop = CreateDropFiles(paths);
            if (NativeMethods.SetClipboardData(CfHdrop, drop) == IntPtr.Zero)
            {
                return false;
            }

            drop = IntPtr.Zero; // owned by the clipboard now
            effectMemory = NativeMethods.GlobalAlloc(GmemMoveable | GmemZeroInit, 4);
            var pointer = NativeMethods.GlobalLock(effectMemory);
            Marshal.WriteInt32(pointer, (int)effect);
            NativeMethods.GlobalUnlock(effectMemory);
            if (NativeMethods.SetClipboardData(PreferredDropEffectFormat, effectMemory) != IntPtr.Zero)
            {
                effectMemory = IntPtr.Zero;
            }

            return true;
        }
        finally
        {
            if (drop != IntPtr.Zero)
            {
                NativeMethods.GlobalFree(drop);
            }

            if (effectMemory != IntPtr.Zero)
            {
                NativeMethods.GlobalFree(effectMemory);
            }

            NativeMethods.CloseClipboard();
        }
    }

    public static ClipboardFiles? GetFiles(IntPtr owner)
    {
        if (!HasFiles() || !TryOpen(owner))
        {
            return null;
        }

        try
        {
            var drop = NativeMethods.GetClipboardData(CfHdrop);
            if (drop == IntPtr.Zero)
            {
                return null;
            }

            var count = NativeMethods.DragQueryFileW(drop, 0xFFFFFFFF, null, 0);
            var paths = new List<string>((int)count);
            for (uint index = 0; index < count; index++)
            {
                var length = NativeMethods.DragQueryFileW(drop, index, null, 0);
                var buffer = new char[length + 1];
                NativeMethods.DragQueryFileW(drop, index, buffer, (uint)buffer.Length);
                paths.Add(new string(buffer, 0, (int)length));
            }

            var effect = ClipboardEffect.Copy;
            var effectMemory = NativeMethods.GetClipboardData(PreferredDropEffectFormat);
            if (effectMemory != IntPtr.Zero && NativeMethods.GlobalSize(effectMemory) >= 4)
            {
                var pointer = NativeMethods.GlobalLock(effectMemory);
                if (pointer != IntPtr.Zero)
                {
                    // DROPEFFECT_MOVE is bit 2; Explorer may combine it with other flags.
                    effect = (Marshal.ReadInt32(pointer) & 2) != 0 ? ClipboardEffect.Move : ClipboardEffect.Copy;
                    NativeMethods.GlobalUnlock(effectMemory);
                }
            }

            return new ClipboardFiles(paths, effect);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    /// <summary>Explorer empties the clipboard after a cut-and-paste completes.</summary>
    public static void Clear(IntPtr owner)
    {
        if (TryOpen(owner))
        {
            NativeMethods.EmptyClipboard();
            NativeMethods.CloseClipboard();
        }
    }

    public static bool SetText(string text, IntPtr owner)
    {
        const uint CfUnicodeText = 13;
        if (!TryOpen(owner))
        {
            return false;
        }

        try
        {
            NativeMethods.EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var memory = NativeMethods.GlobalAlloc(GmemMoveable | GmemZeroInit, (nuint)bytes);
            var pointer = NativeMethods.GlobalLock(memory);
            Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
            NativeMethods.GlobalUnlock(memory);
            if (NativeMethods.SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero)
            {
                NativeMethods.GlobalFree(memory);
                return false;
            }

            return true;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private static bool TryOpen(IntPtr owner)
    {
        // Another app may hold the clipboard for a moment; retry briefly like Explorer does.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (NativeMethods.OpenClipboard(owner))
            {
                return true;
            }

            Thread.Sleep(15);
        }

        return false;
    }

    private static IntPtr CreateDropFiles(IReadOnlyList<string> paths)
    {
        const int HeaderSize = 20; // DROPFILES
        var list = string.Join('\0', paths) + "\0\0";
        var bytes = HeaderSize + list.Length * 2;
        var memory = NativeMethods.GlobalAlloc(GmemMoveable | GmemZeroInit, (nuint)bytes);
        var pointer = NativeMethods.GlobalLock(memory);
        Marshal.WriteInt32(pointer, 0, HeaderSize); // pFiles
        Marshal.WriteInt32(pointer, 4, 0);          // pt.x
        Marshal.WriteInt32(pointer, 8, 0);          // pt.y
        Marshal.WriteInt32(pointer, 12, 0);         // fNC
        Marshal.WriteInt32(pointer, 16, 1);         // fWide
        Marshal.Copy(list.ToCharArray(), 0, pointer + HeaderSize, list.Length);
        NativeMethods.GlobalUnlock(memory);
        return memory;
    }
}

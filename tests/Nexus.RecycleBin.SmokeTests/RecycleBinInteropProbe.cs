using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Nexus.Core.Models;
using Nexus.Core.Services;

namespace Nexus.RecycleBin.SmokeTests;

/// <summary>
/// Invokes the production RecycleBinService property reader on a harmless,
/// ordinary file. A normal file has no System.Recycle.DateDeleted property, but
/// the call still traverses the exact IShellItem2::GetFileTime vtable slot that
/// previously crashed the process when the managed interface was misdeclared.
/// </summary>
internal static class RecycleBinInteropProbe
{
    private static readonly Guid ShellItemInterfaceId =
        new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    public static RecycleBinEntry ReadWithProductionInterop(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var pointer = IntPtr.Zero;
        object? shellItem = null;
        try
        {
            var interfaceId = ShellItemInterfaceId;
            var result = SHCreateItemFromParsingName(
                path,
                IntPtr.Zero,
                ref interfaceId,
                out pointer);
            Marshal.ThrowExceptionForHR(result);

            var shellItemType = typeof(RecycleBinService).GetNestedType(
                "IShellItem",
                BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    "Внутренний контракт IShellItem не найден.");
            shellItem = Marshal.GetTypedObjectForIUnknown(
                pointer,
                shellItemType);
            Marshal.Release(pointer);
            pointer = IntPtr.Zero;

            var createEntry = typeof(RecycleBinService).GetMethod(
                "CreateEntry",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "Внутренний преобразователь Корзины не найден.");
            try
            {
                return (RecycleBinEntry)(createEntry.Invoke(
                    null,
                    [shellItem])
                    ?? throw new InvalidOperationException(
                        "Преобразователь Корзины не вернул результат."));
            }
            catch (TargetInvocationException exception)
                when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
        finally
        {
            if (shellItem is not null && Marshal.IsComObject(shellItem))
            {
                Marshal.FinalReleaseComObject(shellItem);
            }

            if (pointer != IntPtr.Zero)
            {
                Marshal.Release(pointer);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindingContext,
        ref Guid interfaceId,
        out IntPtr item);
}

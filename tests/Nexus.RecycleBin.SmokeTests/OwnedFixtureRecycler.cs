using System.Runtime.InteropServices;

namespace Nexus.RecycleBin.SmokeTests;

/// <summary>
/// Sends only a test-owned file to the Windows Recycle Bin. The explicit
/// FOFX_RECYCLEONDELETE flag requests recycling even if Explorer normally
/// bypasses it; the runner detects and skips when machine policy still refuses.
/// </summary>
internal static class OwnedFixtureRecycler
{
    private const uint FileOperationSilent = 0x0004;
    private const uint FileOperationNoConfirmation = 0x0010;
    private const uint FileOperationNoErrorUi = 0x0400;
    private const uint FileOperationRecycleOnDelete = 0x00080000;
    private const uint FileOperationEarlyFailure = 0x00100000;

    private static readonly Guid ShellItemInterfaceId =
        new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    private static readonly Guid FileOperationClassId =
        new("3AD05575-8857-4850-9277-11B85BDB8E09");

    public static Task RecycleAsync(string fixturePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixturePath);
        if (!File.Exists(fixturePath))
        {
            throw new FileNotFoundException(
                "Тестовый файл для Корзины не найден.",
                fixturePath);
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(
            () =>
            {
                IShellItem? item = null;
                object? operationObject = null;
                try
                {
                    var interfaceId = ShellItemInterfaceId;
                    var result = SHCreateItemFromParsingName(
                        fixturePath,
                        IntPtr.Zero,
                        ref interfaceId,
                        out item);
                    Marshal.ThrowExceptionForHR(result);

                    var operationType = Type.GetTypeFromCLSID(
                        FileOperationClassId,
                        throwOnError: true)
                        ?? throw new InvalidOperationException(
                            "Windows File Operation недоступен.");
                    operationObject = Activator.CreateInstance(operationType)
                        ?? throw new InvalidOperationException(
                            "Windows File Operation не удалось создать.");
                    var operation = (IFileOperation)operationObject;
                    result = operation.SetOperationFlags(
                        FileOperationSilent
                        | FileOperationNoConfirmation
                        | FileOperationNoErrorUi
                        | FileOperationRecycleOnDelete
                        | FileOperationEarlyFailure);
                    Marshal.ThrowExceptionForHR(result);
                    result = operation.DeleteItem(item, IntPtr.Zero);
                    Marshal.ThrowExceptionForHR(result);
                    result = operation.PerformOperations();
                    Marshal.ThrowExceptionForHR(result);
                    result = operation.GetAnyOperationsAborted(out var aborted);
                    Marshal.ThrowExceptionForHR(result);
                    if (aborted)
                    {
                        throw new IOException(
                            "Windows отменила отправку тестового файла в Корзину.");
                    }

                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    ReleaseComObject(operationObject);
                    ReleaseComObject(item);
                }
            })
        {
            IsBackground = true,
            Name = "Nexus Recycle Bin owned-fixture STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public static long? TryGetRecycleBinItemCount()
    {
        var info = new RecycleBinInfo
        {
            Size = (uint)Marshal.SizeOf<RecycleBinInfo>()
        };
        var result = SHQueryRecycleBin(null, ref info);
        return result >= 0 ? info.ItemCount : null;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindingContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHQueryRecycleBin(
        [MarshalAs(UnmanagedType.LPWStr)] string? rootPath,
        ref RecycleBinInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct RecycleBinInfo
    {
        public uint Size;
        public long TotalBytes;
        public long ItemCount;
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(
            IntPtr bindingContext,
            ref Guid handlerId,
            ref Guid interfaceId,
            out IntPtr result);

        [PreserveSig]
        int GetParent(
            [MarshalAs(UnmanagedType.Interface)] out IShellItem parent);

        [PreserveSig]
        int GetDisplayName(uint displayName, out IntPtr name);

        [PreserveSig]
        int GetAttributes(uint mask, out uint attributes);

        [PreserveSig]
        int Compare(
            [MarshalAs(UnmanagedType.Interface)] IShellItem other,
            uint hint,
            out int order);
    }

    [ComImport]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr progressSink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage(
            [MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr progressDialog);
        [PreserveSig] int SetProperties(IntPtr propertyChangeArray);
        [PreserveSig] int SetOwnerWindow(IntPtr ownerWindow);
        [PreserveSig] int ApplyPropertiesToItem(
            [MarshalAs(UnmanagedType.Interface)] IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(
            [MarshalAs(UnmanagedType.Interface)] IShellItem item,
            [MarshalAs(UnmanagedType.LPWStr)] string newName,
            IntPtr progressSink);
        [PreserveSig] int RenameItems(
            IntPtr items,
            [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int MoveItem(
            [MarshalAs(UnmanagedType.Interface)] IShellItem item,
            [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string newName,
            IntPtr progressSink);
        [PreserveSig] int MoveItems(
            IntPtr items,
            [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder);
        [PreserveSig] int CopyItem(
            [MarshalAs(UnmanagedType.Interface)] IShellItem item,
            [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string newName,
            IntPtr progressSink);
        [PreserveSig] int CopyItems(
            IntPtr items,
            [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder);
        [PreserveSig] int DeleteItem(
            [MarshalAs(UnmanagedType.Interface)] IShellItem item,
            IntPtr progressSink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(
            [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder,
            uint attributes,
            [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string templateName,
            IntPtr progressSink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted(
            [MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}

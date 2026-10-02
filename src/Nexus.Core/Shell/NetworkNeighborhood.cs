using System.Runtime.InteropServices;
using Nexus.Core.Interop;
using Nexus.Core.IO;
using Nexus.Core.Threading;

namespace Nexus.Core.Shell;

public enum NetworkDeviceKind
{
    Computer,
    Device
}

/// <param name="ParsingName"><c>\\NAME</c> for computers; a Shell parsing name for media servers, printers, routers.</param>
public sealed record NetworkDevice(string Name, string ParsingName, NetworkDeviceKind Kind);

/// <summary>Computers and devices Windows discovers in the local network — the contents of the "Сеть" folder.</summary>
public static class NetworkNeighborhood
{
    private const string NetworkFolder = "shell:NetworkPlacesFolder";

    /// <summary>Network discovery can take several seconds, so the scan runs on its own STA thread.</summary>
    public static Task<IReadOnlyList<NetworkDevice>> ScanAsync(CancellationToken cancellationToken = default) =>
        StaThread.RunAsync(() => Scan(cancellationToken), "Nexus Network Scan");

    private static IReadOnlyList<NetworkDevice> Scan(CancellationToken cancellationToken)
    {
        if (NativeMethods.TryCreateShellItem(NetworkFolder) is not { } root)
        {
            return [];
        }

        var devices = new List<NetworkDevice>();
        IEnumShellItems? enumerator = null;
        try
        {
            var handler = NativeMethods.EnumItemsHandlerId;
            var interfaceId = NativeMethods.EnumShellItemsInterfaceId;
            if (root.BindToHandler(IntPtr.Zero, ref handler, ref interfaceId, out var pointer) < 0)
            {
                return [];
            }

            try
            {
                enumerator = (IEnumShellItems)Marshal.GetObjectForIUnknown(pointer);
            }
            finally
            {
                Marshal.Release(pointer);
            }

            while (enumerator.Next(1, out var item, out var fetched) == NativeMethods.S_OK && fetched == 1 && item is not null)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = NativeMethods.GetDisplayName(item, Sigdn.NormalDisplay);
                    var parsingName = NativeMethods.GetDisplayName(item, Sigdn.DesktopAbsoluteParsing);
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(parsingName))
                    {
                        var kind = PathHelper.IsNetworkComputer(parsingName) ? NetworkDeviceKind.Computer : NetworkDeviceKind.Device;
                        devices.Add(new NetworkDevice(name, parsingName, kind));
                    }
                }
                finally
                {
                    NativeMethods.Release(item);
                }
            }
        }
        finally
        {
            NativeMethods.Release(enumerator);
            NativeMethods.Release(root);
        }

        return devices
            .OrderBy(device => device.Kind)
            .ThenBy(device => device.Name, NaturalStringComparer.Instance)
            .ToArray();
    }
}

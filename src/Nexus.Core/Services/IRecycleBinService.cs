using Nexus.Core.Models;

namespace Nexus.Core.Services;

public interface IRecycleBinService
{
    Task<IReadOnlyList<RecycleBinEntry>> GetEntriesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores the complete selection as one Windows Shell command. Cancellation
    /// can prevent the command from starting, but cannot stop Shell after it has
    /// begun processing the selection.
    /// </summary>
    Task RestoreAsync(
        IReadOnlyCollection<string> entryIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Irreversibly removes only the selected entries. The caller must obtain
    /// explicit user confirmation before invoking this method. Cancellation can
    /// prevent the native batch from starting, but cannot stop it once started.
    /// </summary>
    Task DeletePermanentlyAsync(
        IReadOnlyCollection<string> entryIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently removes every item in the current user's Windows Recycle Bin.
    /// The caller must obtain explicit user confirmation. Cancellation can prevent
    /// the native call from starting, but cannot stop it once started.
    /// </summary>
    Task EmptyAsync(CancellationToken cancellationToken = default);
}

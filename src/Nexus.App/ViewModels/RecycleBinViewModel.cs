using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nexus.App.Models;
using Nexus.App.Services;
using Nexus.App.Shell;
using Nexus.Core.IO;
using Nexus.Core.Shell;

namespace Nexus.App.ViewModels;

/// <summary>One deleted item in the Recycle Bin.</summary>
public sealed partial class RecycleItemViewModel(RecycleBinEntry entry) : ObservableObject
{
    public RecycleBinEntry Entry { get; } = entry;

    public string Name => Entry.Name;

    public FileKind Kind { get; } = entry.IsFolder ? FileKind.Folder : FileKinds.FromExtension(Path.GetExtension(entry.Name));

    /// <summary>The folder the item was deleted from — where "Восстановить" puts it back.</summary>
    public string OriginalFolder => Path.GetDirectoryName(Entry.OriginalPath ?? string.Empty) ?? string.Empty;

    public string DeletedText => Entry.DeletedAt is { } deleted ? Formatting.DateTime(deleted) : string.Empty;

    public string SizeText => Entry.Size is { } size ? Formatting.Size(size) : string.Empty;

    public string TypeName => Entry.TypeName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    public partial ImageSource? Icon { get; set; }

    public bool HasIcon => Icon is not null;

    public bool IconRequested { get; set; }

    /// <summary>Screen readers announce list items by their string form.</summary>
    public override string ToString() => Name;
}

public enum RecycleSortField
{
    Name,
    Location,
    Deleted,
    Size
}

/// <summary>The Windows Recycle Bin: restore, delete for good, empty.</summary>
public sealed partial class RecycleBinViewModel(RecycleBinService bin, ShellViewModel shell, WindowContext window, DialogService dialogs)
    : ObservableObject
{
    private readonly List<RecycleItemViewModel> _all = [];
    private bool _detached;

    [ObservableProperty]
    public partial ObservableCollection<RecycleItemViewModel> Items { get; private set; } = [];

    public IReadOnlyList<RecycleItemViewModel> SelectedItems { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool HasItems { get; set; }

    [ObservableProperty]
    public partial bool HasSelection { get; set; }

    [ObservableProperty]
    public partial RecycleSortField SortField { get; set; } = RecycleSortField.Deleted;

    [ObservableProperty]
    public partial bool SortDescending { get; set; } = true;

    public async Task LoadAsync()
    {
        IsLoading = true;
        IsEmpty = false;
        try
        {
            var entries = await bin.GetEntriesAsync();
            _all.Clear();
            _all.AddRange(entries.Select(entry => new RecycleItemViewModel(entry)));
            ApplySort();
        }
        catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException)
        {
            shell.NotifyError(exception.Message, "Не удалось прочитать Корзину");
        }
        finally
        {
            IsLoading = false;
            HasItems = _all.Count > 0;
            IsEmpty = _all.Count == 0;
            SetSelection([]);
        }
    }

    /// <summary>The page was left: later results must not overwrite the next page's status bar.</summary>
    public void Detach() => _detached = true;

    public void SetSelection(IReadOnlyList<RecycleItemViewModel> items)
    {
        SelectedItems = items;
        HasSelection = items.Count > 0;
        UpdateStatus();
    }

    /// <summary>Clicking the same column again flips the order; dates and sizes start with the largest.</summary>
    public void SortBy(RecycleSortField field)
    {
        if (SortField == field)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortField = field;
            SortDescending = field is RecycleSortField.Deleted or RecycleSortField.Size;
        }

        ApplySort();
    }

    public Task RestoreSelectedAsync() => RestoreAsync(SelectedItems);

    public Task RestoreAllAsync() => RestoreAsync(_all.ToArray());

    public async Task DeleteSelectedAsync()
    {
        var items = SelectedItems;
        if (items.Count == 0)
        {
            return;
        }

        var message = items.Count == 1
            ? $"«{items[0].Name}» будет удалён без возможности восстановления."
            : $"{Formatting.Items(items.Count)} будут удалены без возможности восстановления.";
        if (!await dialogs.ConfirmAsync("Удалить навсегда?", message, "Удалить", destructive: true))
        {
            return;
        }

        await RunAsync(() => bin.DeletePermanentlyAsync(Ids(items), window.Handle),
            items.Count == 1 ? $"«{items[0].Name}» удалён навсегда." : $"Удалено навсегда: {Formatting.Items(items.Count)}.");
    }

    public async Task EmptyAsync()
    {
        if (_all.Count == 0)
        {
            return;
        }

        var message = $"{Formatting.Items(_all.Count)} ({Formatting.Size(TotalSize)}) будут удалены без возможности восстановления.";
        if (!await dialogs.ConfirmAsync("Очистить корзину?", message, "Очистить", destructive: true))
        {
            return;
        }

        await RunAsync(bin.EmptyAsync, "Корзина очищена.");
    }

    private long TotalSize => _all.Sum(item => item.Entry.Size ?? 0);

    private async Task RestoreAsync(IReadOnlyList<RecycleItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var message = items.Count == 1
            ? $"«{items[0].Name}» возвращён в «{items[0].OriginalFolder}»."
            : $"Восстановлено: {Formatting.Items(items.Count)}.";
        await RunAsync(() => bin.RestoreAsync(Ids(items), window.Handle), message);
    }

    private async Task RunAsync(Func<Task> operation, string successMessage)
    {
        try
        {
            await operation();
            shell.Notify(successMessage, InfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            shell.NotifyError(exception.Message);
        }

        await LoadAsync();
    }

    private static string[] Ids(IEnumerable<RecycleItemViewModel> items) => items.Select(item => item.Entry.Id).ToArray();

    private void ApplySort()
    {
        IEnumerable<RecycleItemViewModel> sorted = SortField switch
        {
            RecycleSortField.Name => _all.OrderBy(item => item.Name, NaturalStringComparer.Instance),
            RecycleSortField.Location => _all.OrderBy(item => item.OriginalFolder, NaturalStringComparer.Instance)
                .ThenBy(item => item.Name, NaturalStringComparer.Instance),
            RecycleSortField.Size => _all.OrderBy(item => item.Entry.Size ?? -1),
            _ => _all.OrderBy(item => item.Entry.DeletedAt ?? DateTimeOffset.MinValue)
        };

        Items = new ObservableCollection<RecycleItemViewModel>(SortDescending ? sorted.Reverse() : sorted);
    }

    private void UpdateStatus()
    {
        if (_detached)
        {
            return;
        }

        shell.StatusText = _all.Count == 0 ? string.Empty : $"{Formatting.Items(_all.Count)} · {Formatting.Size(TotalSize)}";
        if (SelectedItems.Count == 0)
        {
            shell.SelectionText = string.Empty;
            return;
        }

        var size = Formatting.Size(SelectedItems.Sum(item => item.Entry.Size ?? 0));
        shell.SelectionText = SelectedItems.Count == 1
            ? $"Выбран 1 элемент: {size}"
            : $"Выбрано {Formatting.Items(SelectedItems.Count)}: {size}";
    }
}

using Nexus.Core.IO;
using Nexus.Core.Shell;

namespace Nexus.Core.Operations;

/// <summary>Something Nexus did that can be reversed with Ctrl+Z.</summary>
public abstract record UndoableAction(string Description, DateTimeOffset At);

public sealed record RenamedAction(string OldPath, string NewPath, DateTimeOffset At)
    : UndoableAction($"Переименование «{Path.GetFileName(NewPath)}»", At);

public sealed record CreatedAction(string CreatedPath, DateTimeOffset At)
    : UndoableAction($"Создание «{Path.GetFileName(CreatedPath)}»", At);

public sealed record CopiedAction(IReadOnlyList<string> CreatedPaths, DateTimeOffset At)
    : UndoableAction($"Копирование ({CreatedPaths.Count})", At);

public sealed record MovedAction(IReadOnlyList<(string From, string To)> Moves, DateTimeOffset At)
    : UndoableAction($"Перемещение ({Moves.Count})", At);

public sealed record RecycledAction(IReadOnlyList<string> OriginalPaths, DateTimeOffset At)
    : UndoableAction($"Удаление в Корзину ({OriginalPaths.Count})", At);

/// <summary>
/// The last actions performed in Nexus and how to reverse them. Undo never deletes
/// anything permanently: undoing a copy or a creation sends the new items to the Recycle Bin.
/// </summary>
public sealed class UndoHistory(FileOperationService operations, RecycleBinService recycleBin)
{
    private const int Capacity = 30;
    private readonly LinkedList<UndoableAction> _actions = new();
    private readonly Lock _gate = new();

    public event EventHandler? Changed;

    public UndoableAction? Last
    {
        get
        {
            lock (_gate)
            {
                return _actions.Last?.Value;
            }
        }
    }

    public void Record(UndoableAction action)
    {
        lock (_gate)
        {
            _actions.AddLast(action);
            while (_actions.Count > Capacity)
            {
                _actions.RemoveFirst();
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reverses the most recent action. Returns a message for the UI.</summary>
    public async Task<string> UndoLastAsync(IntPtr owner)
    {
        UndoableAction? action;
        lock (_gate)
        {
            action = _actions.Last?.Value;
            if (action is not null)
            {
                _actions.RemoveLast();
            }
        }

        if (action is null)
        {
            return "Нечего отменять.";
        }

        Changed?.Invoke(this, EventArgs.Empty);
        switch (action)
        {
            case RenamedAction renamed:
                if (!File.Exists(renamed.NewPath) && !Directory.Exists(renamed.NewPath))
                {
                    return "Отмена невозможна: элемент уже переименован или удалён.";
                }

                FileOperationService.Rename(renamed.NewPath, Path.GetFileName(renamed.OldPath));
                return $"Отменено: {action.Description}";

            case CreatedAction created:
                await operations.RecycleAsync([created.CreatedPath], owner);
                return $"Отменено: {action.Description}";

            case CopiedAction copied:
                await operations.RecycleAsync(copied.CreatedPaths.Where(path => File.Exists(path) || Directory.Exists(path)).ToArray(), owner);
                return $"Отменено: {action.Description}. Копии перемещены в Корзину.";

            case MovedAction moved:
                foreach (var group in moved.Moves.GroupBy(move => PathHelper.GetParent(move.From) ?? move.From, StringComparer.OrdinalIgnoreCase))
                {
                    var existing = group.Select(move => move.To).Where(path => File.Exists(path) || Directory.Exists(path)).ToArray();
                    await operations.MoveAsync(existing, group.Key, owner);
                }

                return $"Отменено: {action.Description}";

            case RecycledAction recycled:
                var restored = await recycleBin.RestoreRecentlyDeletedAsync(recycled.OriginalPaths, recycled.At, owner);
                return restored == 0
                    ? "Не удалось найти удалённые элементы в Корзине."
                    : $"Восстановлено из Корзины: {restored}";

            default:
                return "Это действие нельзя отменить.";
        }
    }
}

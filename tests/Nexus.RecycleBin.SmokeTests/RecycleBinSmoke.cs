using Nexus.Core.Models;
using Nexus.Core.Services;

namespace Nexus.RecycleBin.SmokeTests;

internal static class RecycleBinSmoke
{
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("RecycleBin smoke: пропущено вне Windows.");
            return 0;
        }

        try
        {
            var service = new RecycleBinService();

            RunNonDestructiveInteropProbe();

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await ExpectCanceledAsync(
                    service.GetEntriesAsync(cancellation.Token));
                const string validButMissingId =
                    "recycle:0000000000000000000000000000000000000000000000000000000000000000";
                await ExpectCanceledAsync(
                    service.RestoreAsync(
                        [validButMissingId],
                        cancellation.Token));
                await ExpectCanceledAsync(
                    service.DeletePermanentlyAsync(
                        [validButMissingId],
                        cancellation.Token));
                await ExpectCanceledAsync(
                    service.EmptyAsync(cancellation.Token));
            }

            // These are deliberate no-ops. This smoke test never deletes or
            // empties anything from the user's real Recycle Bin.
            await service.RestoreAsync([]);
            await service.DeletePermanentlyAsync([]);

            ExpectArgumentException(
                () => service.RestoreAsync(["not-a-recycle-id"]));
            ExpectArgumentException(
                () => service.DeletePermanentlyAsync([" "]));

            var first = await service.GetEntriesAsync();
            ValidateEntries(first);

            // A second pass is intentional: it exercises COM teardown and a new
            // STA/apartment initialization, which previously exposed the invalid
            // IShellItem2 vtable declaration as an AccessViolationException.
            var second = await service.GetEntriesAsync();
            ValidateEntries(second);

            var concurrentReads = await Task.WhenAll(
                Enumerable.Range(0, 4)
                    .Select(_ => service.GetEntriesAsync()));
            foreach (var read in concurrentReads)
            {
                ValidateEntries(read);
            }

            if (args.Contains("--fixture", StringComparer.OrdinalIgnoreCase))
            {
                await RunOwnedFixtureAsync(service);
            }

            var driveRoots = second
                .Select(entry => TryGetRoot(entry.OriginalPath))
                .Where(root => root is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(root => root, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Console.WriteLine(
                $"RecycleBin smoke: OK; элементов={second.Count}; "
                + $"источников={driveRoots.Length}. Ничего не изменено.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"RecycleBin smoke: FAILED: {exception}");
            return 1;
        }
    }

    private static void RunNonDestructiveInteropProbe()
    {
        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            "Nexus.RecycleBin.InteropProbe",
            Guid.NewGuid().ToString("N"));
        var fixturePath = Path.Combine(fixtureRoot, "probe.txt");
        const string content = "Nexus Recycle Bin ABI probe";
        Directory.CreateDirectory(fixtureRoot);
        File.WriteAllText(fixturePath, content);

        try
        {
            var entry = RecycleBinInteropProbe.ReadWithProductionInterop(
                fixturePath);
            Check(
                entry.Name is "probe.txt" or "probe",
                $"Shell вернула неверное имя probe-файла: '{entry.Name}'.");
            Check(
                entry.SizeBytes == content.Length,
                $"IShellItem2::GetUInt64 вернул неверный размер probe-файла: {entry.SizeBytes?.ToString() ?? "null"}.");
            Check(
                entry.DeletedAt is null,
                "Обычный файл не должен иметь System.Recycle.DateDeleted.");
            Check(
                entry.RecycledPath is not null
                && PathsEqual(entry.RecycledPath, fixturePath),
                "Shell вернула неверный физический путь probe-файла.");
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }
        }
    }

    private static async Task RunOwnedFixtureAsync(
        IRecycleBinService service)
    {
        // Windows can bypass the Recycle Bin for files inside the OS temporary
        // directory. Keep the owned fixture under the current test workspace so
        // the Shell exercises its normal recycle path.
        var fixtureRoot = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".nexus-recycle-smoke",
            Guid.NewGuid().ToString("N"));
        var fixturePath = Path.Combine(
            fixtureRoot,
            $"nexus-recycle-{Guid.NewGuid():N}.txt");
        var expectedContent = Guid.NewGuid().ToString("N");
        RecycleBinEntry? fixtureEntry = null;

        Directory.CreateDirectory(fixtureRoot);
        await File.WriteAllTextAsync(fixturePath, expectedContent);

        try
        {
            var itemCountBefore =
                OwnedFixtureRecycler.TryGetRecycleBinItemCount();
            await OwnedFixtureRecycler.RecycleAsync(fixturePath);
            Check(
                !File.Exists(fixturePath),
                "Тестовый файл не был отправлен в Корзину.");

            IReadOnlyList<RecycleBinEntry> entries = [];
            for (var attempt = 0; attempt < 20; attempt++)
            {
                entries = await service.GetEntriesAsync();
                fixtureEntry = entries.SingleOrDefault(
                    entry => PathsEqual(entry.OriginalPath, fixturePath));
                if (fixtureEntry is not null)
                {
                    break;
                }

                await Task.Delay(100);
            }

            ValidateEntries(entries);
            if (fixtureEntry is null
                && itemCountBefore is not null
                && OwnedFixtureRecycler.TryGetRecycleBinItemCount()
                    == itemCountBefore)
            {
                Console.WriteLine(
                    "RecycleBin fixture: пропущено — политика Windows "
                    + "не создала элемент Корзины даже с FOFX_RECYCLEONDELETE.");
                return;
            }

            Check(
                fixtureEntry is not null,
                "Windows Shell не вернула только что удалённый тестовый файл.");
            var resolvedEntry = fixtureEntry
                ?? throw new InvalidOperationException(
                    "Тестовый элемент Корзины не найден.");
            Check(
                resolvedEntry.DeletedAt is not null,
                "Для тестового файла не прочитана дата удаления.");
            Check(
                resolvedEntry.SizeBytes == expectedContent.Length,
                "Для тестового файла неверно прочитан размер.");

            await service.RestoreAsync([resolvedEntry.Id]);
            Check(
                File.Exists(fixturePath),
                "Тестовый файл не был восстановлен.");
            Check(
                await File.ReadAllTextAsync(fixturePath) == expectedContent,
                "Содержимое восстановленного тестового файла изменилось.");
        }
        finally
        {
            // A failed assertion must not leave our own fixture in the user's
            // Recycle Bin. Restoration is recoverable and is the only mutation
            // attempted by this cleanup path.
            if (!File.Exists(fixturePath) && fixtureEntry is not null)
            {
                try
                {
                    await service.RestoreAsync([fixtureEntry.Id]);
                }
                catch
                {
                    // Preserve the original test failure. The GUID filename makes
                    // any leftover fixture unambiguous and harmless.
                }
            }

            if (File.Exists(fixturePath))
            {
                File.Delete(fixturePath);
            }

            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }

            var fixtureParent = Directory.GetParent(fixtureRoot)?.FullName;
            if (fixtureParent is not null
                && Directory.Exists(fixtureParent)
                && !Directory.EnumerateFileSystemEntries(fixtureParent).Any())
            {
                Directory.Delete(fixtureParent);
            }
        }
    }

    private static void ValidateEntries(
        IReadOnlyCollection<RecycleBinEntry> entries)
    {
        var duplicateId = entries
            .GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        Check(
            duplicateId is null,
            $"Повторяющийся идентификатор: {duplicateId?.Key}");

        foreach (var entry in entries)
        {
            Check(
                entry.Id.StartsWith("recycle:", StringComparison.Ordinal)
                && entry.Id.Length == "recycle:".Length + 64,
                "Некорректный идентификатор элемента Корзины.");
            Check(
                !string.IsNullOrWhiteSpace(entry.Name),
                "Элемент Корзины не должен иметь пустое имя.");
            Check(
                !string.IsNullOrWhiteSpace(entry.DisplayType),
                "Элемент Корзины не должен иметь пустой отображаемый тип.");
            Check(
                entry.SizeBytes is null or >= 0,
                "Размер элемента Корзины не может быть отрицательным.");
            Check(
                entry.DeletedAt is null
                || entry.DeletedAt.Value.Offset >= TimeSpan.FromHours(-14)
                && entry.DeletedAt.Value.Offset <= TimeSpan.FromHours(14),
                "Дата удаления имеет некорректное смещение часового пояса.");
        }
    }

    private static async Task ExpectCanceledAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException(
            "Предварительно отменённое перечисление должно завершаться отменой.");
    }

    private static void ExpectArgumentException(Func<Task> action)
    {
        try
        {
            _ = action();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException(
            "Некорректный идентификатор должен отклоняться до вызова Windows Shell.");
    }

    private static string? TryGetRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetPathRoot(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left)
            || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

using System.Collections.Concurrent;

namespace Nexus.Core.Threading;

/// <summary>
/// Runs tasks on a fixed set of single-threaded-apartment threads. Windows Shell
/// objects (icons, thumbnails, shortcuts, the Recycle Bin) expect STA callers.
/// </summary>
public sealed class StaTaskScheduler : TaskScheduler, IDisposable
{
    private readonly BlockingCollection<Task> _queue = new();
    private readonly List<Thread> _threads;

    public StaTaskScheduler(int threadCount, string name)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadCount);
        _threads = Enumerable.Range(0, threadCount).Select(index =>
        {
            var thread = new Thread(Run)
            {
                IsBackground = true,
                Name = $"{name} STA {index + 1}"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return thread;
        }).ToList();
    }

    public override int MaximumConcurrencyLevel => _threads.Count;

    public Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken = default) =>
        Task.Factory.StartNew(work, cancellationToken, TaskCreationOptions.DenyChildAttach, this);

    public Task Run(Action work, CancellationToken cancellationToken = default) =>
        Task.Factory.StartNew(work, cancellationToken, TaskCreationOptions.DenyChildAttach, this);

    public void Dispose()
    {
        _queue.CompleteAdding();
        foreach (var thread in _threads)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }

        _queue.Dispose();
    }

    protected override void QueueTask(Task task) => _queue.Add(task);

    protected override IEnumerable<Task> GetScheduledTasks() => _queue.ToArray();

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
        Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
        && _threads.Contains(Thread.CurrentThread)
        && TryExecuteTask(task);

    private void Run()
    {
        foreach (var task in _queue.GetConsumingEnumerable())
        {
            TryExecuteTask(task);
        }
    }
}

/// <summary>Starts a one-off STA thread for long, blocking shell work such as file operations.</summary>
public static class StaThread
{
    public static Task<T> RunAsync<T>(Func<T> work, string name)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(work());
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = name
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}

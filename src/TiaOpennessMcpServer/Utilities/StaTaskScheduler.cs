using System.Collections.Concurrent;

namespace TiaOpennessMcpServer.Utilities;

/// <summary>
/// Runs tasks on a dedicated STA thread. TIA Openness uses COM under the hood
/// and requires all calls to originate from the same STA thread.
/// </summary>
public sealed class StaTaskScheduler : IDisposable
{
    private readonly Thread _staThread;
    private readonly BlockingCollection<(Action action, TaskCompletionSource<bool> tcs)> _queue = new();
    private volatile bool _disposed;

    public StaTaskScheduler()
    {
        _staThread = new Thread(ThreadLoop)
        {
            Name         = "TIA-Openness-STA",
            IsBackground = true,
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
    }

    /// <summary>Runs <paramref name="action"/> on the STA thread and awaits completion.</summary>
    public Task RunAsync(Action action)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StaTaskScheduler));
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add((action, tcs));
        return tcs.Task;
    }

    /// <summary>Runs <paramref name="func"/> on the STA thread and returns its result.</summary>
    public Task<T> RunAsync<T>(Func<T> func)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StaTaskScheduler));
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add((() =>
        {
            try   { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, new TaskCompletionSource<bool>()));
        return tcs.Task;
    }

    /// <summary>Runs <paramref name="func"/> on the STA thread and returns its result (async overload).</summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> func)
    {
        T result = default!;
        Exception? caught = null;

        await RunAsync(() =>
        {
            try   { result = func().GetAwaiter().GetResult(); }
            catch (Exception ex) { caught = ex; }
        });

        if (caught is not null) throw caught;
        return result;
    }

    private void ThreadLoop()
    {
        foreach (var (action, tcs) in _queue.GetConsumingEnumerable())
        {
            try
            {
                action();
                tcs.TrySetResult(false);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        _staThread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }
}

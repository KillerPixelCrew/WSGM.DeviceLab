using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.DeviceLab.Inventory;

/// <summary>Runs one synchronous provider call at a time without making caller cancellation wait.</summary>
internal sealed class CancellableSynchronousWorker
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal T Run<T>(
        Func<CancellationToken, T> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        _gate.Wait(cancellationToken);

        Task<T> worker;
        try
        {
            worker = Task.Run(() => operation(cancellationToken));
        }
        catch
        {
            _gate.Release();
            throw;
        }

        _ = worker.ContinueWith(
            static (completed, state) =>
            {
                // A caller may already have observed cancellation. Inspecting Exception keeps a
                // later bounded-provider failure from surfacing as an unobserved task exception.
                _ = completed.Exception;
                ((SemaphoreSlim)state!).Release();
            },
            _gate,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return worker.WaitAsync(cancellationToken).GetAwaiter().GetResult();
    }
}

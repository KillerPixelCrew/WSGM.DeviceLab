using WSGM.DeviceLab.Inventory;

namespace WSGM.Device.Tests;

public sealed class CancellableSynchronousWorkerTests
{
    [Fact]
    public async Task CancellationReturnsBeforeAnUncooperativeSynchronousProviderFinishes()
    {
        var worker = new CancellableSynchronousWorker();
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim release = new();
        using CancellationTokenSource cancellation = new();
        Task<int> call = Task.Run(() => worker.Run(
            _ =>
            {
                started.Set();
                release.Wait();
                return 1;
            },
            cancellation.Token));

        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await call.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            release.Set();
        }

        int next = await Task.Run(() => worker.Run(_ => 2, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, next);
    }
}

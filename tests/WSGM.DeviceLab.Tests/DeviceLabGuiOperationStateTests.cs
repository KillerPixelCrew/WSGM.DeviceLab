using WSGM.DeviceLab.Gui;

namespace WSGM.Device.Tests;

public sealed class DeviceLabGuiOperationStateTests
{
    [Fact]
    public void LaterFailurePreservesLastSuccessfulResult()
    {
        DeviceLabGuiOperationState state = DeviceLabGuiOperationState.Initial
            .Started()
            .Succeeded("first result")
            .Started()
            .Failed("malformed plugin");

        Assert.Equal("first result", state.LastSuccessfulResult);
        Assert.Equal("Operation failed: malformed plugin", state.StatusText);
        Assert.False(state.IsRunning);
    }

    [Fact]
    public void CancellationPreservesLastSuccessfulResult()
    {
        DeviceLabGuiOperationState state = DeviceLabGuiOperationState.Initial
            .Succeeded("last good result")
            .Started()
            .Cancelled();

        Assert.Equal("last good result", state.LastSuccessfulResult);
        Assert.Equal("Operation cancelled.", state.StatusText);
        Assert.False(state.IsRunning);
    }

    [Fact]
    public void StartingAnotherOperationDoesNotReplaceVisibleSuccess()
    {
        DeviceLabGuiOperationState state = DeviceLabGuiOperationState.Initial
            .Succeeded("durable result")
            .Started();

        Assert.Equal("durable result", state.LastSuccessfulResult);
        Assert.Equal("Working…", state.StatusText);
        Assert.True(state.IsRunning);
    }
}

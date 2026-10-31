using Xunit;

namespace ComputerUse.TimeTracking.Tests;

// Uses named kernel objects: runs on Windows only.
public sealed class SingleInstanceGuardTests
{
    private readonly string _name = "ComputerUse.TimeTracking.Tests." + Guid.NewGuid().ToString("N");

    [Fact]
    public void SecondAcquire_IsNotPrimary()
    {
        if (!OperatingSystem.IsWindows()) return;

        using SingleInstanceGuard first = SingleInstanceGuard.Acquire(_name);
        SingleInstanceGuard second = RunOnOtherThread(() => SingleInstanceGuard.Acquire(_name));

        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);
        second.Dispose();
    }

    [Fact]
    public async Task SignalPrimary_RaisesTheRequestedActivation()
    {
        if (!OperatingSystem.IsWindows()) return;

        using SingleInstanceGuard primary = SingleInstanceGuard.Acquire(_name);
        var received = new TaskCompletionSource<ActivationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ListenForActivation(request => received.TrySetResult(request));

        Assert.True(SingleInstanceGuard.SignalPrimary(ActivationRequest.OpenSettings, _name));
        Assert.Equal(ActivationRequest.OpenSettings, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void SignalPrimary_WithoutPrimary_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.False(SingleInstanceGuard.SignalPrimary(ActivationRequest.Show, _name));
    }

    // A mutex is re-entrant on its owning thread, so the second instance must acquire elsewhere.
    private static T RunOnOtherThread<T>(Func<T> action)
    {
        T result = default!;
        var thread = new Thread(() => result = action());
        thread.Start();
        thread.Join();
        return result;
    }
}

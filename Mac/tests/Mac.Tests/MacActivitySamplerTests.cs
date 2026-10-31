using ComputerUse.TimeTracking.Providers;
using Xunit;

namespace ComputerUse.TimeTracking.Tests;

/// <summary>Runs against the real desktop, so it asserts shape and safety rather than a specific foreground app.</summary>
public sealed class MacActivitySamplerTests
{
    [Fact]
    public void GetIdleTime_IsNeverNegative()
    {
        var sampler = new MacActivitySampler();

        Assert.True(sampler.GetIdleTime() >= TimeSpan.Zero);
    }

    [Fact]
    public void Sample_ReturnsAProcessNameWithoutThrowing()
    {
        var sampler = new MacActivitySampler();

        var sample = sampler.Sample();

        Assert.NotNull(sample);
        if (sample.ProcessName.Length > 0) Assert.Equal(sample.ProcessName, sample.DisplayName);
        Assert.Null(sample.Email);
    }
}

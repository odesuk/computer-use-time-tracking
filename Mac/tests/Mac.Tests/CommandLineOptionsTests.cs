using Xunit;

namespace ComputerUse.TimeTracking.Tests;

public sealed class CommandLineOptionsTests
{
    [Fact]
    public void SetupFlag_IsRecognisedAndRemovedFromHostArgs()
    {
        CommandLineOptions options = CommandLineOptions.Parse(new[] { "--setup", "--urls", "http://localhost:0" });

        Assert.True(options.Setup);
        Assert.Equal(new[] { "--urls", "http://localhost:0" }, options.HostArgs);
    }

    [Fact]
    public void NoArguments_LeavesSetupOff()
    {
        CommandLineOptions options = CommandLineOptions.Parse(Array.Empty<string>());

        Assert.False(options.Setup);
        Assert.Empty(options.HostArgs);
    }
}

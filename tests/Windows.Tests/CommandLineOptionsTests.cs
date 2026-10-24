using Xunit;

namespace ComputerUse.TimeTracking.Tests;

public sealed class CommandLineOptionsTests
{
    [Fact]
    public void KnownSwitches_AreParsed_AndOthersPassThrough()
    {
        CommandLineOptions options = CommandLineOptions.Parse(new[] { "--SETUP", "--future-option", "/unknown", "--environment", "Development" });

        Assert.True(options.Setup);
        Assert.Equal(new[] { "--future-option", "/unknown", "--environment", "Development" }, options.HostArgs);
    }

    [Fact]
    public void NoArguments_StartsTheTracker()
    {
        CommandLineOptions options = CommandLineOptions.Parse(Array.Empty<string>());

        Assert.False(options.Setup);
        Assert.Empty(options.HostArgs);
    }
}

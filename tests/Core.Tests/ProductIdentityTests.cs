using Xunit;

namespace ComputerUse.TimeTracking.Tests;

public sealed class ProductIdentityTests
{
    [Fact]
    public void StandaloneIdentity_UsesDistinctLocalStorageAndProcessNames()
    {
        ProductIdentity identity = ProductIdentity.Standalone;

        Assert.Equal("Computer Use Time Tracker", identity.DisplayName);
        Assert.Equal("ComputerUse-TimeTracking", identity.DataDirectoryName);
        Assert.Equal("ComputerUse.TimeTracking", identity.SingleInstanceName);
        Assert.NotEqual("TimeTracker", identity.DataDirectoryName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("nested/folder")]
    public void Validate_RejectsUnsafeDataDirectoryNames(string value)
    {
        ProductIdentity identity = ProductIdentity.Standalone with { DataDirectoryName = value };

        Assert.Throws<ArgumentException>(identity.Validate);
    }
}

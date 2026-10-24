using ComputerUse.TimeTracking.Reports;
using Xunit;

namespace ComputerUse.TimeTracking.Tests;

public sealed class DurationTextTests
{
    [Theory]
    [InlineData(0, "0 m")]
    [InlineData(20, "< 1 m")]
    [InlineData(30, "1 m")]
    [InlineData(89, "1 m")]
    [InlineData(90, "2 m")]
    [InlineData(38 * 60, "38 m")]
    [InlineData(3600, "1 h 00 m")]
    [InlineData(2 * 3600 + 55 * 60 + 10, "2 h 55 m")]
    [InlineData(3599 + 30, "1 h 00 m")]
    public void Format(long seconds, string expected) => Assert.Equal(expected, DurationText.Format(seconds));
}

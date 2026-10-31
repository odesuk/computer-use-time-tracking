using ComputerUse.TimeTracking.Providers;
using Xunit;

namespace ComputerUse.TimeTracking.Tests;

public sealed class NewOutlookUIAutomationProviderTests
{
    [Theory]
    [InlineData("Calendar - jane@firm.com - Outlook", true)]
    [InlineData("People", true)]
    [InlineData("To Do - Outlook", true)]
    [InlineData("RE: Calendar invite for the deposition", false)]
    [InlineData("Inbox - jane@firm.com - Outlook", false)]
    public void NonMailViews_AreNotTreatedAsEmail(string title, bool expected)
    {
        Assert.Equal(expected, NewOutlookUIAutomationProvider.IsNonMailView(title));
    }
}

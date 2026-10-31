using ComputerUse.TimeTracking.Tracking;
using Xunit;

namespace ComputerUse.TimeTracking.Tests;

public sealed class WindowTitleNormalizerTests
{
    [Theory]
    [InlineData("(3) Inbox - Gmail - Google Chrome", "Inbox - Gmail")]
    [InlineData("(99+) Inbox - Gmail - Google Chrome", "Inbox - Gmail")]
    [InlineData("● Brief.docx - Word", "Brief.docx")]
    [InlineData("* Notes.txt - Notepad", "Notes.txt")]
    [InlineData("Notes.txt* - Notepad", "Notes.txt")]
    [InlineData("Motion.pdf - Adobe Acrobat Reader (64-bit)", "Motion.pdf")]
    [InlineData("Motion.pdf - Adobe Acrobat Pro (64-bit)", "Motion.pdf")]
    [InlineData("Chat | Jane Doe | Microsoft Teams", "Chat | Jane Doe")]
    [InlineData("Docket and 3 more pages - Work - Microsoft​ Edge", "Docket")]
    [InlineData("Docket - Personal - Microsoft Edge", "Docket")]
    [InlineData("Smith v. Jones - Court Docket - Microsoft Edge", "Smith v. Jones - Court Docket")]
    [InlineData("Budget.xlsx - Excel", "Budget.xlsx")]
    [InlineData("Plain title", "Plain title")]
    [InlineData("Word", "Word")]
    [InlineData("  spaced   title  ", "spaced title")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Normalize(string? raw, string expected)
    {
        Assert.Equal(expected, WindowTitleNormalizer.Normalize(raw));
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        string once = WindowTitleNormalizer.Normalize("(3) ● Inbox - Work - Google Chrome");
        Assert.Equal(once, WindowTitleNormalizer.Normalize(once));
    }
}

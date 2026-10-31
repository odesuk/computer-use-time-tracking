using System.Xml.Linq;
using Xunit;

namespace ComputerUse.TimeTracking.Tests;

public sealed class LoginItemTests
{
    [Fact]
    public void BuildPlist_IsWellFormedAndRunsTheExecutableAtLogin()
    {
        string executable = "/Applications/Computer Use & Time.app/Contents/MacOS/ComputerUse.TimeTracking";

        XDocument document = XDocument.Parse(LoginItem.BuildPlist(LoginItem.Label, executable));
        string label = document.Descendants("key").First(k => k.Value == "Label").ElementsAfterSelf("string").First().Value;
        string program = document.Descendants("array").Single().Element("string")!.Value;

        Assert.Equal(LoginItem.Label, label);
        Assert.Equal(executable, program);
        Assert.Contains(document.Descendants("key"), k => k.Value == "RunAtLoad");
    }

    [Fact]
    public void PlistPath_IsInTheUserLaunchAgentsFolder()
    {
        Assert.EndsWith(Path.Combine("Library", "LaunchAgents", LoginItem.Label + ".plist"), LoginItem.PlistPath);
    }
}

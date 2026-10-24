namespace ComputerUse.TimeTracking;

/// <summary>Parsed command line. Unrecognised arguments are passed through to the host.</summary>
internal sealed record CommandLineOptions(bool Setup, string[] HostArgs)
{
    public static CommandLineOptions Parse(IEnumerable<string> args)
    {
        bool setup = false;
        var hostArgs = new List<string>();
        foreach (string argument in args)
        {
            if (Is(argument, "--setup")) setup = true;
            else hostArgs.Add(argument);
        }

        return new CommandLineOptions(setup, hostArgs.ToArray());
    }

    private static bool Is(string argument, string option) =>
        string.Equals(argument, option, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(argument, "/" + option[2..], StringComparison.OrdinalIgnoreCase);
}

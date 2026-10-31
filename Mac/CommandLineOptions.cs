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
            if (string.Equals(argument, "--setup", StringComparison.OrdinalIgnoreCase)) setup = true;
            else hostArgs.Add(argument);
        }

        return new CommandLineOptions(setup, hostArgs.ToArray());
    }
}

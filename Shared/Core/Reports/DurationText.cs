namespace ComputerUse.TimeTracking.Reports;

public static class DurationText
{
    /// <summary>"2 h 55 m", "38 m", "&lt; 1 m"; rounded to the nearest minute.</summary>
    public static string Format(long seconds)
    {
        if (seconds <= 0) return "0 m";
        if (seconds < 30) return "< 1 m";

        long minutes = (seconds + 30) / 60;
        long hours = minutes / 60;
        long rest = minutes % 60;
        return hours == 0 ? $"{rest} m" : $"{hours} h {rest:00} m";
    }
}

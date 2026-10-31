namespace ComputerUse.TimeTracking.Reports;

/// <summary>Converts a local calendar date to the UTC range it covers (23 or 25 hours on DST days).</summary>
public static class LocalDay
{
    public static (DateTime UtcStart, DateTime UtcEnd) ToUtcRange(DateOnly date, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        return (StartOfDayUtc(date, timeZone), StartOfDayUtc(date.AddDays(1), timeZone));
    }

    public static DateOnly Today(TimeProvider timeProvider) =>
        DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);

    private static DateTime StartOfDayUtc(DateOnly date, TimeZoneInfo timeZone)
    {
        // A few zones switch DST at midnight, so 00:00 may not exist; the day then starts at the
        // first valid local time.
        DateTime local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        for (int guard = 0; guard < 4 && timeZone.IsInvalidTime(local); guard++)
        {
            local = local.AddMinutes(30);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
    }
}

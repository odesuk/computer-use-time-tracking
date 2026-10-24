namespace ComputerUse.TimeTracking;

/// <summary>Compile-time product identity supplied by the executable that hosts the tracker.</summary>
public sealed record ProductIdentity(
    string DisplayName,
    string ShortName,
    string DataDirectoryName,
    string DefaultReportDirectoryName,
    string ReportFilePrefix,
    string SingleInstanceName,
    string StartupTaskId,
    string ExecutableProcessName,
    string PrivacyNotice,
    string? ActiveIconResourceName = null,
    string? PausedIconResourceName = null)
{
    public static ProductIdentity Standalone { get; } = new(
        "Computer Use Time Tracker",
        "Computer Use",
        "ComputerUse-TimeTracking",
        "Computer Use Time Tracking Reports",
        "Computer Use Activity",
        "ComputerUse.TimeTracking",
        "ComputerUseTimeTracking",
        "ComputerUse.TimeTracking",
        "Activity stays on this computer and is never sent to a cloud service.");

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ShortName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ReportFilePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(SingleInstanceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(StartupTaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ExecutableProcessName);
        ValidateDirectoryName(DataDirectoryName, nameof(DataDirectoryName));
        ValidateDirectoryName(DefaultReportDirectoryName, nameof(DefaultReportDirectoryName));
    }

    private static void ValidateDirectoryName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("A product directory name must be one safe path segment.", parameterName);
        }
    }
}

/// <summary>Process-wide identity. Executables configure it before any storage or UI is created.</summary>
public static class ProductContext
{
    public static ProductIdentity Current { get; private set; } = ProductIdentity.Standalone;

    public static void Configure(ProductIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        Current = identity;
    }
}

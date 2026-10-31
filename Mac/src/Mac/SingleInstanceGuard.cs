using ComputerUse.TimeTracking.Native;

namespace ComputerUse.TimeTracking;

/// <summary>
/// One tracker per user session. Two trackers would each record the same foreground window and the report
/// would double-count it. The lock is an advisory flock on a file in the data directory, so it is released by
/// the OS even if the process crashes.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly IntPtr _file;

    private SingleInstanceGuard(IntPtr file, bool isPrimary)
    {
        _file = file;
        IsPrimary = isPrimary;
    }

    public bool IsPrimary { get; }

    public static SingleInstanceGuard Acquire(string lockPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        IntPtr file = MacNative.OpenForAppend(lockPath);
        if (file == IntPtr.Zero) throw new IOException($"The instance lock could not be opened: {lockPath}");

        if (MacNative.TryLockExclusive(MacNative.DescriptorOf(file))) return new SingleInstanceGuard(file, isPrimary: true);

        MacNative.Close(file);
        return new SingleInstanceGuard(IntPtr.Zero, isPrimary: false);
    }

    public void Dispose()
    {
        if (_file != IntPtr.Zero) MacNative.Close(_file);
    }
}

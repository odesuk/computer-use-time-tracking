using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ComputerUse.TimeTracking.Native;

public static class Win32Native
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    /// <summary>Title of <paramref name="handle"/>, at full length (not truncated at 255 characters).</summary>
    public static string GetWindowTitle(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return string.Empty;

        int length = GetWindowTextLength(handle);
        if (length <= 0) return string.Empty;

        var buffer = new StringBuilder(length + 1);
        return GetWindowText(handle, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    public static TimeSpan GetIdleTime()
    {
        var lastInput = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lastInput)) return TimeSpan.Zero;

        // Both counters wrap every ~49.7 days; unsigned subtraction handles the wrap.
        uint envTicks = (uint)Environment.TickCount;
        uint idleTicks = envTicks - lastInput.dwTime;
        return TimeSpan.FromMilliseconds(idleTicks);
    }
}

using System.Runtime.InteropServices;
using System.Text;

namespace ComputerUse.TimeTracking.Native;

/// <summary>
/// The few C entry points the tracker needs from the Objective-C runtime, Accessibility, Quartz, and
/// Core Foundation. Every binding is a plain C function; no AppKit types are referenced directly.
/// </summary>
internal static class MacNative
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibC = "/usr/lib/libSystem.B.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const uint Utf8Encoding = 0x08000100;          // kCFStringEncodingUTF8
    private const uint CombinedSessionState = 0;           // kCGEventSourceStateCombinedSessionState
    private const uint AnyInputEventType = 0xFFFFFFFF;     // kCGAnyInputEventType
    private const int LockExclusive = 2;                   // LOCK_EX
    private const int LockNonBlocking = 4;                 // LOCK_NB
    private const int RtldLazy = 0x1;                      // RTLD_LAZY

    private static readonly Lazy<IntPtr> FocusedWindowAttribute = new(() => CreateCFString("AXFocusedWindow"));
    private static readonly Lazy<IntPtr> TitleAttribute = new(() => CreateCFString("AXTitle"));
    private static readonly Lazy<IntPtr> ScreenLockedKey = new(() => CreateCFString("CGSSessionScreenIsLocked"));

    // NSWorkspace is only registered once AppKit is loaded. The Avalonia app loads it itself; a plain host
    // such as a test run does not, so load it here before the first lookup.
    private static readonly Lazy<IntPtr> AppKitHandle = new(() => dlopen(AppKit, RtldLazy));

    public sealed record ForegroundApp(int ProcessId, string Name);

    /// <summary>The frontmost application, or null when there is none (for example during a fast user switch).</summary>
    public static ForegroundApp? FrontmostApplication()
    {
        if (AppKitHandle.Value == IntPtr.Zero) return null;

        IntPtr workspaceClass = objc_getClass("NSWorkspace");
        IntPtr workspace = SendPointer(workspaceClass, sel_registerName("sharedWorkspace"));
        IntPtr app = workspace == IntPtr.Zero ? IntPtr.Zero : SendPointer(workspace, sel_registerName("frontmostApplication"));
        if (app == IntPtr.Zero) return null;

        int processId = SendInt32(app, sel_registerName("processIdentifier"));
        string name = NSStringValue(SendPointer(app, sel_registerName("localizedName")));
        return new ForegroundApp(processId, name);
    }

    /// <summary>
    /// Title of the focused window of <paramref name="processId"/>. Needs Accessibility permission for this
    /// app; without it the call fails and the title is empty, and tracking continues by application only.
    /// </summary>
    public static string FocusedWindowTitle(int processId)
    {
        IntPtr application = AXUIElementCreateApplication(processId);
        if (application == IntPtr.Zero) return string.Empty;

        try
        {
            if (CopyAttribute(application, FocusedWindowAttribute.Value, out IntPtr window) != 0 || window == IntPtr.Zero)
            {
                return string.Empty;
            }

            try
            {
                if (CopyAttribute(window, TitleAttribute.Value, out IntPtr title) != 0 || title == IntPtr.Zero)
                {
                    return string.Empty;
                }

                try
                {
                    return CFStringToString(title);
                }
                finally
                {
                    CFRelease(title);
                }
            }
            finally
            {
                CFRelease(window);
            }
        }
        finally
        {
            CFRelease(application);
        }
    }

    public static bool IsAccessibilityTrusted() => AXIsProcessTrusted();

    /// <summary>Seconds since the last keyboard or mouse event in this login session.</summary>
    public static TimeSpan SecondsSinceLastInput()
    {
        double seconds = CGEventSourceSecondsSinceLastEventType(CombinedSessionState, AnyInputEventType);
        return double.IsFinite(seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
    }

    /// <summary>True while the screen is locked. Read from the session dictionary, which is how the lock screen reports itself.</summary>
    public static bool IsScreenLocked()
    {
        IntPtr session = CGSessionCopyCurrentDictionary();
        if (session == IntPtr.Zero) return false;

        try
        {
            IntPtr value = CFDictionaryGetValue(session, ScreenLockedKey.Value);
            return value != IntPtr.Zero
                && CFGetTypeID(value) == CFBooleanGetTypeID()
                && CFBooleanGetValue(value);
        }
        finally
        {
            CFRelease(session);
        }
    }

    public static IntPtr PushAutoreleasePool() => objc_autoreleasePoolPush();

    public static void PopAutoreleasePool(IntPtr pool) => objc_autoreleasePoolPop(pool);

    /// <summary>
    /// Takes an exclusive, non-blocking advisory lock on <paramref name="descriptor"/>. The lock lives as long as
    /// the descriptor is open and is released by the OS when the process exits, including after a crash.
    /// </summary>
    public static bool TryLockExclusive(int descriptor) => flock(descriptor, LockExclusive | LockNonBlocking) == 0;

    /// <summary>Opens (creating if needed) a file and returns its C stdio handle, or zero on failure.</summary>
    public static IntPtr OpenForAppend(string path) => fopen(path, "a+");

    public static int DescriptorOf(IntPtr file) => fileno(file);

    public static void Close(IntPtr file) => fclose(file);

    private static string NSStringValue(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero) return string.Empty;
        IntPtr utf8 = SendPointer(nsString, sel_registerName("UTF8String"));
        return utf8 == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(utf8) ?? string.Empty;
    }

    private static string CFStringToString(IntPtr cfString)
    {
        long length = CFStringGetLength(cfString);
        if (length <= 0) return string.Empty;

        // UTF-8 needs at most four bytes per UTF-16 code unit, plus the terminating zero.
        var buffer = new byte[length * 4 + 1];
        if (!CFStringGetCString(cfString, buffer, buffer.Length, Utf8Encoding)) return string.Empty;

        int end = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    private static IntPtr CreateCFString(string value) => CFStringCreateWithCString(IntPtr.Zero, value, Utf8Encoding);

    private static int CopyAttribute(IntPtr element, IntPtr attribute, out IntPtr value) =>
        AXUIElementCopyAttributeValue(element, attribute, out value);

    // Objective-C runtime. The objc_msgSend variants differ only in return type; the platform ABI makes
    // the integer and pointer returns safe to share a calling convention for these selectors.
    [DllImport(ObjC, EntryPoint = "objc_getClass", CharSet = CharSet.Ansi)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC, EntryPoint = "sel_registerName", CharSet = CharSet.Ansi)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPointer(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern int SendInt32(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_autoreleasePoolPush")]
    private static extern IntPtr objc_autoreleasePoolPush();

    [DllImport(ObjC, EntryPoint = "objc_autoreleasePoolPop")]
    private static extern void objc_autoreleasePoolPop(IntPtr pool);

    // Accessibility. Boolean is a one-byte C type, so the managed result is marshalled as U1.
    [DllImport(AppServices)]
    private static extern IntPtr AXUIElementCreateApplication(int processId);

    [DllImport(AppServices)]
    private static extern int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);

    [DllImport(AppServices)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool AXIsProcessTrusted();

    // Quartz event source and session state.
    [DllImport(CoreGraphics)]
    private static extern double CGEventSourceSecondsSinceLastEventType(uint stateId, uint eventType);

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGSessionCopyCurrentDictionary();

    // Core Foundation.
    [DllImport(CoreFoundation, CharSet = CharSet.Ansi)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern long CFStringGetLength(IntPtr value);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CFStringGetCString(IntPtr value, byte[] buffer, long bufferSize, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);

    [DllImport(CoreFoundation)]
    private static extern ulong CFGetTypeID(IntPtr value);

    [DllImport(CoreFoundation)]
    private static extern ulong CFBooleanGetTypeID();

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CFBooleanGetValue(IntPtr value);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);

    // libc.
    [DllImport(LibC, CharSet = CharSet.Ansi)]
    private static extern IntPtr dlopen(string path, int mode);

    [DllImport(LibC)]
    private static extern int flock(int descriptor, int operation);

    [DllImport(LibC, CharSet = CharSet.Ansi)]
    private static extern IntPtr fopen(string path, string mode);

    [DllImport(LibC)]
    private static extern int fileno(IntPtr file);

    [DllImport(LibC)]
    private static extern int fclose(IntPtr file);
}


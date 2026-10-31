namespace ComputerUse.TimeTracking;

/// <summary>What a second launch asks the running instance to do.</summary>
internal enum ActivationRequest
{
    /// <summary>A plain second launch: tell the user the tracker is already running.</summary>
    Show,

    /// <summary><c>--setup</c>: open Settings in the running instance, which owns the live settings.</summary>
    OpenSettings,
}

/// <summary>
/// One tracker per Windows session. Two trackers would each record the same foreground window and
/// the report would double-count it. <c>Local\</c> scopes the names to the session, so fast user
/// switching gives each signed-in user their own tracker.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const string DefaultName = "ComputerUse.TimeTracking";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _showEvent;
    private readonly EventWaitHandle? _settingsEvent;
    private readonly List<RegisteredWaitHandle> _registrations = new();

    private SingleInstanceGuard(Mutex mutex, bool isPrimary, string name)
    {
        _mutex = mutex;
        IsPrimary = isPrimary;
        if (isPrimary)
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(name, ActivationRequest.Show));
            _settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(name, ActivationRequest.OpenSettings));
        }
    }

    public bool IsPrimary { get; }

    /// <param name="name">Overridable so tests don't collide with a running tracker.</param>
    public static SingleInstanceGuard Acquire(string name = DefaultName)
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{name}.Instance", out bool createdNew);
        return new SingleInstanceGuard(mutex, createdNew, name);
    }

    /// <summary>Called by a secondary instance before it exits. False if no primary is listening.</summary>
    public static bool SignalPrimary(ActivationRequest request, string name = DefaultName)
    {
        if (!EventWaitHandle.TryOpenExisting(EventName(name, request), out EventWaitHandle? handle)) return false;
        using (handle) return handle.Set();
    }

    /// <summary>
    /// Primary instance: run <paramref name="onActivate"/> whenever another launch signals.
    /// The callback runs on a thread-pool thread; marshal to the dispatcher before touching UI.
    /// </summary>
    public void ListenForActivation(Action<ActivationRequest> onActivate)
    {
        ArgumentNullException.ThrowIfNull(onActivate);
        if (_showEvent is null || _settingsEvent is null || _registrations.Count > 0) return;
        _registrations.Add(ThreadPool.RegisterWaitForSingleObject(
            _showEvent, (_, _) => onActivate(ActivationRequest.Show), null, Timeout.Infinite, executeOnlyOnce: false));
        _registrations.Add(ThreadPool.RegisterWaitForSingleObject(
            _settingsEvent, (_, _) => onActivate(ActivationRequest.OpenSettings), null, Timeout.Infinite, executeOnlyOnce: false));
    }

    public void Dispose()
    {
        foreach (RegisteredWaitHandle registration in _registrations) registration.Unregister(null);
        _registrations.Clear();
        _showEvent?.Dispose();
        _settingsEvent?.Dispose();
        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Disposed on a thread that doesn't own the mutex; the OS releases it at exit.
            }
        }

        _mutex.Dispose();
    }

    private static string EventName(string name, ActivationRequest request) => $@"Local\{name}.{request}";
}

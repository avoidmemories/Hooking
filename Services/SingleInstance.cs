using System.Threading;

namespace Hooking.Services;

/// <summary>
/// Keeps one interface process alive at a time.
///
/// Two instances are genuinely harmful here, not merely untidy: each one spawns
/// its own engine process, the engines fight over the same keys, and each
/// instance writes its own in-memory settings over the other's, so options
/// appear to reset themselves.
///
/// A second launch does not just refuse to start. While the application lives in the
/// tray its window is hidden, so the second launch asks the first one to bring the
/// window back.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Hooking.SingleInstance";
    private const string ShowEventName = @"Local\Hooking.ShowWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showRegistration;

    /// <summary>
    /// True when this process is the first instance and may continue.
    /// </summary>
    public bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
            return created;
        }
        catch
        {
            // If the guard itself fails, let the app run rather than refusing to start.
            return true;
        }
    }

    /// <summary>
    /// Called by the first instance. Runs <paramref name="onShowRequested"/> each time
    /// a later launch asks for the window. The callback runs on a thread-pool thread.
    /// </summary>
    public void ListenForShowRequests(Action onShowRequested)
    {
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _showRegistration = ThreadPool.RegisterWaitForSingleObject(
                _showEvent,
                (_, _) => onShowRequested(),
                state: null,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch
        {
            // Without the listener a second launch simply does nothing visible.
        }
    }

    /// <summary>
    /// Called by a later launch, which then exits. Asks the running instance to show
    /// its window.
    /// </summary>
    public static void RequestShow()
    {
        try
        {
            using var showEvent = EventWaitHandle.OpenExisting(ShowEventName);
            showEvent.Set();
        }
        catch
        {
            // The running instance is gone or not listening; there is nothing to show.
        }
    }

    public void Dispose()
    {
        _showRegistration?.Unregister(null);
        _showRegistration = null;
        _showEvent?.Dispose();
        _showEvent = null;

        if (_mutex is null)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch
        {
        }

        _mutex.Dispose();
        _mutex = null;
    }
}

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using SnapHook.Models;

namespace SnapHook.Services;

/// <summary>
/// Owns the lifecycle of the native snap tap worker.
///
/// The engine lives in its own process on purpose. A low level keyboard hook is
/// only allowed to run for <c>LowLevelHooksTimeout</c> (200 ms by default), after
/// which Windows silently removes it and the keyboard starts behaving oddly. A
/// WPF process cannot promise that budget while collecting garbage or laying out
/// the UI, so the hook is kept away from the interface entirely.
/// </summary>
public sealed class SnapHookWorkerService(AppPaths paths) : IDisposable
{
    private Process? _process;
    private string _shutdownEventName = "Local\\SnapHookWorkerStop";

    /// <summary>
    /// Every scan code the current configuration manages.
    ///
    /// Kept so the keys can be released from here if the worker is taken down
    /// without unwinding: in blocking mode a key it injected would otherwise stay
    /// down in the system and the character would keep moving.
    /// </summary>
    private readonly List<int> _armedScanCodes = [];

    /// <summary>
    /// Bumped on every start and stop.
    ///
    /// Stopping returns immediately and finishes in the background, so a cleanup
    /// belonging to an earlier stop could otherwise land after a newer worker had
    /// been launched and shut it down — which showed up as the engine starting and
    /// dying at once, leaving an empty log.
    /// </summary>
    private int _generation;

    /// <summary>
    /// The configuration the engine was last started with, and whether it should be
    /// running. Used by <see cref="EnsureRunning"/> to bring it back after it stops.
    /// </summary>
    private AppSettings? _current;
    private bool _wantRunning;
    private DateTime _lastStart = DateTime.MinValue;

    public WorkerState State { get; private set; } = WorkerState.Stopped;

    public string? LastError { get; private set; }

    public string LastCommandLine { get; private set; } = string.Empty;

    /// <summary>
    /// Restarts the worker with the supplied configuration. Called on every
    /// settings change; the restart is cheap because the process is tiny.
    /// </summary>
    public void Apply(AppSettings settings)
    {
        Stop();

        _armedScanCodes.Clear();
        _current = settings;
        _wantRunning = false;
        _lastStart = DateTime.UtcNow;

        if (!settings.SnapHookEnabled)
        {
            State = WorkerState.Stopped;
            LastError = null;
            return;
        }

        // A fresh name per start keeps a stale event from a previous worker out of
        // the way.
        _shutdownEventName = $"Local\\SnapHookWorkerStop_{Environment.ProcessId}_{Guid.NewGuid():N}";

        // Claims the generation, so any cleanup still pending from the previous stop
        // sees that it is out of date and leaves this worker alone.
        _generation++;

        var arguments = BuildArguments(settings, out var profileCount);
        if (profileCount == 0)
        {
            State = WorkerState.Stopped;
            LastError = "No usable key pairs. Add at least one pair to arm snap tap.";
            return;
        }

        _wantRunning = true;

        try
        {
            var executable = paths.WorkerExecutable;
            if (!File.Exists(executable))
            {
                State = WorkerState.Failed;
                LastError = "Worker executable was not built.";
                return;
            }

            LastCommandLine = $"\"{executable}\" {arguments}";

            _process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            State = _process is null ? WorkerState.Failed : WorkerState.Running;
            LastError = _process is null ? "Worker process did not start." : null;
        }
        catch (Exception exception)
        {
            State = WorkerState.Failed;
            LastError = exception.Message;
        }
    }

    /// <summary>
    /// Starts the worker again when it should be running but has stopped, for example
    /// because it was started before the desktop was ready and failed, or it exited.
    /// Called on a timer that keeps running while the window is in the tray.
    /// </summary>
    public void EnsureRunning()
    {
        if (!_wantRunning || _current is null)
        {
            return;
        }

        if (_process is not null && !_process.HasExited)
        {
            return;
        }

        // A short pause between attempts, so a worker that exits at once does not
        // get restarted in a tight loop.
        if (DateTime.UtcNow - _lastStart < TimeSpan.FromSeconds(3))
        {
            return;
        }

        Apply(_current);
    }

    /// <summary>
    /// Builds the command line. One <c>--profile</c> per game, each carrying its
    /// own scan code pairs, so a movement game and a driving game can coexist.
    /// </summary>
    private string BuildArguments(AppSettings settings, out int profileCount)
    {
        var builder = new StringBuilder();
        profileCount = 0;

        foreach (var profile in settings.ActiveTargets())
        {
            var pairs = BuildPairs(profile.Presets);
            if (pairs.Length == 0)
            {
                continue;
            }

            // The method travels inside the profile, so every game keeps its own
            // regardless of what any other profile is set to.
            builder.Append("--profile \"")
                .Append(profile.ProcessName)
                .Append('|')
                .Append(EngineModes.ToToken(profile.Mode))
                .Append('|')
                .Append(pairs)
                .Append("\" ");

            profileCount++;
        }

        builder.Append("--stop-event \"").Append(_shutdownEventName).Append("\" ");

        return builder.ToString().TrimEnd();
    }

    private string BuildPairs(IEnumerable<KeyPairPreset> presets)
    {
        var parts = new List<string>();

        foreach (var preset in presets)
        {
            // The full key identity, not the bare scan code: it carries the E0
            // marker, which is the only thing separating right Ctrl from left
            // Ctrl, or the keypad's 0 from Insert.
            var first = KeyNames.ToKeyId(preset.FirstKey);
            var second = KeyNames.ToKeyId(preset.SecondKey);

            if (first == 0 || second == 0 || first == second)
            {
                continue;
            }

            _armedScanCodes.Add(first & 0xFF);
            _armedScanCodes.Add(second & 0xFF);
            parts.Add($"{first}:{second}");
        }

        return string.Join(',', parts);
    }

    /// <summary>
    /// Stops the worker without holding up the interface.
    ///
    /// The old version signalled the worker and then waited up to 1.2 seconds for it
    /// to exit, on the caller's thread. That caller is the click handler, so every
    /// disarm froze the window for about a second — which is what the delay on the
    /// switch was.
    ///
    /// The wait is still needed, because a worker that is killed outright can leave
    /// an injected key held down. It is moved off the interface thread instead: the
    /// process is signalled at once, the UI carries on, and a background task cleans
    /// up when the worker has actually gone.
    /// </summary>
    public void Stop()
    {
        var process = _process;
        var eventName = _shutdownEventName;
        _process = null;

        // The generation this cleanup belongs to. If a newer worker is started
        // before the cleanup runs, the counter will have moved on and the cleanup
        // will leave it alone.
        var generation = ++_generation;

        if (process is null)
        {
            KillOrphans();
            State = WorkerState.Stopped;
            return;
        }

        var armed = _armedScanCodes.ToList();
        _armedScanCodes.Clear();

        // Ask it to unwind now. This is instant: it only sets an event.
        SignalStop(eventName, 0, null);

        // The finishing touches happen off the interface thread, so the click that
        // triggered them is not held up by the wait.
        Task.Run(() =>
        {
            try
            {
                if (!process.WaitForExit(1200))
                {
                    // It did not leave on its own, so it is taken down. Its own
                    // release pass will not have run, which is why the keys are
                    // released below.
                    process.Kill();
                    process.WaitForExit(600);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }

            // A newer worker has been started since, so this cleanup must not touch
            // anything: killing orphans now would take that one down with it.
            if (generation != _generation)
            {
                return;
            }

            KillOrphans();

            // Releases anything the worker may not have let go of.
            if (armed.Count > 0)
            {
                KeyInjector.ReleaseAll(armed);
            }
        });

        State = WorkerState.Stopped;
    }

    /// <summary>
    /// Opens the worker's stop event and sets it, giving the worker a chance to
    /// finish. Returns false when the event could not be opened or the process
    /// did not exit in time.
    /// </summary>
    private static bool SignalStop(string eventName, int timeoutMilliseconds, Process? process = null)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenEvent(EventModifyState, false, eventName);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            if (!SetEvent(handle))
            {
                return false;
            }

            // A null process means "do not wait": the caller is on the interface
            // thread and the wait happens elsewhere.
            return process is null || process.WaitForExit(timeoutMilliseconds);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Removes workers left behind by a previous crash of the interface.
    /// </summary>
    private static void KillOrphans()
    {
        foreach (var process in Process.GetProcessesByName("SnapHookWorker"))
        {
            try
            {
                // Its own stop event name is unknown here, so a leftover worker
                // is released through the interface side as well before it goes.
                process.Kill();
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private const uint EventModifyState = 0x0002;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenEvent(uint desiredAccess, bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

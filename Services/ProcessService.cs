using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Hooking.Models;

namespace Hooking.Services;

/// <summary>
/// Enumerates the applications a profile can target. Games are windowed
/// processes, so the list is built from top level windows rather than from the
/// process table: that filters out services and background noise.
/// </summary>
public sealed class ProcessService
{
    public IReadOnlyList<OpenAppInfo> ListRunning()
    {
        var result = new Dictionary<string, OpenAppInfo>(StringComparer.OrdinalIgnoreCase);

        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle))
            {
                return true;
            }

            GetWindowThreadProcessId(handle, out var pid);
            if (pid == 0 || pid == Environment.ProcessId)
            {
                return true;
            }

            string processName;
            string? path = null;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                processName = process.ProcessName;
                // MainModule can be surprisingly slow (and can fail for a process of
                // a different bitness). QueryFullProcessImageName only asks Windows
                // for the image path and is sufficient for the profile UI.
                path = TryGetPathById((int)pid);
            }
            catch
            {
                return true;
            }

            if (processName.Equals("Hooking", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("HookingWorker", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var title = GetTitle(handle);
            if (result.TryGetValue(processName, out var existing))
            {
                if (string.IsNullOrWhiteSpace(existing.WindowTitle) && !string.IsNullOrWhiteSpace(title))
                {
                    result[processName] = existing with { WindowTitle = title };
                }

                return true;
            }

            result[processName] = new OpenAppInfo(processName, path, title, IsRunning: true);
            return true;
        }, IntPtr.Zero);

        return result.Values
            .OrderBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Well known games, offered so a profile can be created before the game is
    /// ever launched.
    /// </summary>
    public static IReadOnlyList<OpenAppInfo> KnownGames() =>
    [
        new("TslGame", null, "PUBG: Battlegrounds", false),
        new("cs2", null, "Counter-Strike 2", false),
        new("r5apex", null, "Apex Legends", false),
        new("VALORANT-Win64-Shipping", null, "VALORANT", false),
        new("FortniteClient-Win64-Shipping", null, "Fortnite", false),
        new("GTA5", null, "Grand Theft Auto V", false),
        new("RainbowSix", null, "Rainbow Six Siege", false),
        new("Overwatch", null, "Overwatch 2", false),
        new("RustClient", null, "Rust", false),
        new("EscapeFromTarkov", null, "Escape from Tarkov", false),
        new("Terraria", null, "Terraria", false),
        new("javaw", null, "Minecraft: Java Edition", false)
    ];


    /// <summary>
    /// A readable name for a process, when its window title cannot be trusted.
    ///
    /// Two builds of the same game share a window title — the legacy and the enhanced
    /// GTA V both call themselves "Grand Theft Auto V" — but their executables differ,
    /// which is the one thing that distinguishes them. This turns the image name into
    /// something presentable while keeping that difference visible.
    /// </summary>
    public static string DescribeProcess(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return string.Empty;
        }

        // Marker suffixes that only say how the build was compiled, not which game it
        // is, so they are dropped rather than shown.
        var trimmed = processName;
        foreach (var suffix in new[] { "-Win64-Shipping", "-Win64-Shipping-Cmd", "_BE", "-BE" })
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^suffix.Length];
            }
        }

        // Split on the separators a process name uses, and capitalise each word.
        var words = trimmed
            .Split(['_', '-', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]);

        var readable = string.Join(' ', words);

        return string.IsNullOrWhiteSpace(readable) ? processName : readable;
    }

    public static string? TryGetPath(string processName)
    {
        try
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    var path = TryGetPathById(process.Id);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        return path;
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    public static bool IsRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string GetTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string? TryGetPathById(int processId)
    {
        const uint ProcessQueryLimitedInformation = 0x1000;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            // The documented maximum Win32 path length is 32,767 characters.
            var path = new StringBuilder(32768);
            var length = (uint)path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref length)
                ? path.ToString()
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process,
        uint flags,
        StringBuilder executablePath,
        ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

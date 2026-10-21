using System.Runtime.InteropServices;

namespace Hooking.Services;

/// <summary>
/// Minimal key injection used by the interface side, for one job only: making
/// sure no key is left logically held when the engine goes away.
///
/// In blocking mode the engine owns the stream, so a press it injected would
/// stay down in the system after the worker process is gone, and the game would
/// believe the key is still held. Releasing them from here before the process is
/// killed closes that hole.
/// </summary>
public static class KeyInjector
{
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfScancode = 0x0008;
    private const uint KeyeventfExtended = 0x0001;

    /// <summary>
    /// Scan codes that travel with the E0 prefix. Must match the table in the
    /// native worker.
    /// </summary>
    private static readonly HashSet<int> ExtendedScanCodes =
    [
        0x1C, 0x1D, 0x35, 0x37, 0x38, 0x47, 0x48, 0x49,
        0x4B, 0x4D, 0x4F, 0x50, 0x51, 0x52, 0x53, 0x5B, 0x5C, 0x5D
    ];

    /// <summary>
    /// Releases every supplied scan code. Sending a release for a key that is
    /// not down is harmless, so this can be called unconditionally.
    /// </summary>
    public static void ReleaseAll(IEnumerable<int> scanCodes)
    {
        var inputs = new List<INPUT>();

        foreach (var scanCode in scanCodes.Distinct())
        {
            if (scanCode == 0)
            {
                continue;
            }

            uint flags = KeyeventfScancode | KeyeventfKeyup;
            if (ExtendedScanCodes.Contains(scanCode))
            {
                flags |= KeyeventfExtended;
            }

            inputs.Add(new INPUT
            {
                type = InputKeyboard,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = (ushort)scanCode,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = UIntPtr.Zero
                    }
                }
            });
        }

        if (inputs.Count > 0)
        {
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
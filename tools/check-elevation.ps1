# Reports whether each running process is elevated, and whether a
# non-elevated low level keyboard hook could observe keystrokes aimed at it.
#
# The reliable probe for "can I interact with this process" is asking for
# PROCESS_VM_READ. A medium integrity process is denied that on an elevated one,
# which is exactly the condition that stops a hook from seeing its input.

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Probe {
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern IntPtr OpenProcess(int acc, bool inherit, int pid);
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr h);

    const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int PROCESS_VM_READ = 0x0010;

    public static string Check(int pid) {
        IntPtr q = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (q == IntPtr.Zero)
            return "QUERY denied (err " + Marshal.GetLastWin32Error() + ")";
        CloseHandle(q);

        IntPtr v = OpenProcess(PROCESS_VM_READ, false, pid);
        if (v == IntPtr.Zero) {
            int err = Marshal.GetLastWin32Error();
            if (err == 5)
                return "ELEVATED (denied at medium integrity) -> hook cannot see it";
            return "VM_READ denied err " + err;
        }
        CloseHandle(v);
        return "same level as this process -> hook works";
    }
}
"@

foreach ($name in @('TslGame', 'SnapHook', 'SnapHookWorker', 'explorer')) {
    $procs = Get-Process -Name $name -ErrorAction SilentlyContinue
    if (-not $procs) { Write-Output ("{0,-14} not running" -f $name); continue }
    foreach ($p in $procs) {
        Write-Output ("{0,-14} pid {1,-7} {2}" -f $name, $p.Id, [Probe]::Check($p.Id))
    }
}

$principal = New-Object System.Security.Principal.WindowsPrincipal(
    [System.Security.Principal.WindowsIdentity]::GetCurrent())
Write-Output ""
Write-Output ("this checker   elevated = {0}" -f $principal.IsInRole(
    [System.Security.Principal.WindowsBuiltInRole]::Administrator))

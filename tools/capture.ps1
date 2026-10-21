param(
    [string]$ProcessName = "Hooking",
    [string]$OutFile = "C:\Users\gysar\source\repos\ONEKEY\Hooking\tools\shot.png"
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@

$proc = Get-Process -Name $ProcessName -ErrorAction Stop | Select-Object -First 1
$handle = $proc.MainWindowHandle
if ($handle -eq [IntPtr]::Zero) { Write-Error "no main window"; exit 1 }

[Win]::ShowWindow($handle, 9) | Out-Null
[Win]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Milliseconds 900

$rect = New-Object Win+RECT
[Win]::GetWindowRect($handle, [ref]$rect) | Out-Null

$width = $rect.R - $rect.L
$height = $rect.B - $rect.T

$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($rect.L, $rect.T, 0, 0, $bitmap.Size)
$bitmap.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)

$graphics.Dispose()
$bitmap.Dispose()

Write-Output "$OutFile ${width}x${height}"
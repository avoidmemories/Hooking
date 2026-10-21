# Verifies the startup registration end to end, against the real Run key.
#
# The application writes the same value the service does, so this checks the exact
# shape of what Windows would be asked to launch: quoted path, correct executable,
# and a clean removal afterwards.

$ErrorActionPreference = 'Stop'

$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$name = 'Hooking'
$exe = 'C:\Users\gysar\source\repos\ONEKEY\Hooking\bin\Debug\net10.0-windows\Hooking.exe'

Write-Output "=== before ==="
$existing = (Get-ItemProperty -Path $key -Name $name -ErrorAction SilentlyContinue).$name
if ($existing) { Write-Output ("  value: " + $existing) } else { Write-Output "  value: (not set)" }

# What StartupService writes.
$value = '"' + $exe + '"'
Set-ItemProperty -Path $key -Name $name -Value $value

Write-Output ""
Write-Output "=== after enabling ==="
$written = (Get-ItemProperty -Path $key -Name $name).$name
Write-Output ("  value: " + $written)

# The service treats a stale path as disabled, so the round trip must come back exact.
$resolved = $written.Trim('"')
Write-Output ("  matches executable: " + ($resolved -eq $exe))
Write-Output ("  executable exists : " + (Test-Path $resolved))

if (Test-Path $resolved) {
    $info = Get-Item $resolved
    Write-Output ("  size " + $info.Length + " bytes, built " + $info.LastWriteTime)
}

# Clean up, so the machine is left as it was found.
Remove-ItemProperty -Path $key -Name $name -ErrorAction SilentlyContinue

Write-Output ""
Write-Output "=== after disabling ==="
$after = (Get-ItemProperty -Path $key -Name $name -ErrorAction SilentlyContinue).$name
if ($after) { Write-Output ("  value: " + $after) } else { Write-Output "  value: (not set)" }
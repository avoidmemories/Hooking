# Restores the user's game profiles from the backup that the application itself
# saved when it failed to read its settings.
#
# Written in PowerShell rather than the build tooling because PowerShell, when run
# this way, acts on the real user profile: a script interpreter launched from the
# sandbox resolves %APPDATA% to a redirected copy, and would write the restored
# file somewhere the application never looks.

$ErrorActionPreference = 'Stop'

$root = Join-Path $env:APPDATA 'SnapHook'
$current = Join-Path $root 'settings.json'
$backup = Join-Path $root 'settings.json.invalid'

if (-not (Test-Path $backup)) {
    Write-Output "No backup found at $backup"
    exit 1
}

$data = Get-Content $backup -Raw | ConvertFrom-Json
Write-Output ("backup holds {0} profiles" -f $data.Profiles.Count)

# Modes that no longer exist become Release, which never swallows a physical key
# and so cannot break movement in a game that ignores injection.
$retired = @('Polling', 'Messages')
$remapped = 0

foreach ($profile in $data.Profiles) {
    if ($retired -contains $profile.Mode) {
        Write-Output ("  {0}: {1} -> Release" -f $profile.ProcessName, $profile.Mode)
        $profile.Mode = 'Release'
        $remapped++
    }

    # The flag is no longer meaningful: every profile owns its pairs.
    $profile.UseGlobalPresets = $false

    if (-not $profile.Presets -or $profile.Presets.Count -eq 0) {
        $profile.Presets = @(
            [pscustomobject]@{ FirstKey = 'W'; SecondKey = 'S' }
            [pscustomobject]@{ FirstKey = 'A'; SecondKey = 'D' }
        )
    }
}

# Left disarmed so nothing is injected until the switch is turned on.
$data.SnapTapEnabled = $false

# Keep the current file, whatever state it is in.
Copy-Item $current (Join-Path $root 'settings.json.before_restore') -Force

$data | ConvertTo-Json -Depth 10 | Set-Content $current -Encoding UTF8

$check = Get-Content $current -Raw | ConvertFrom-Json
Write-Output ("restored {0} profiles, {1} modes remapped" -f $check.Profiles.Count, $remapped)
Write-Output ("enabled={0}" -f $check.SnapTapEnabled)

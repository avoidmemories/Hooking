using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace Hooking.Services;

/// <summary>
/// Registers the application to launch with Windows.
///
/// The entry is a Task Scheduler task that runs with the highest available rights.
/// The application requires administrator rights (app.manifest), and a plain entry in
/// the Run key cannot be used for that: at sign in Windows would need to show a UAC
/// prompt, nobody is there to answer it, and the application silently never starts.
/// A scheduled task with RunLevel HighestAvailable starts elevated without a prompt.
/// </summary>
public sealed class StartupService
{
    private const string TaskName = "Hooking";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Entry written by the Run key version of this feature.</summary>
    private const string RunValueName = "Hooking";

    /// <summary>Entry written by earlier builds, under the previous name.</summary>
    private const string LegacyValueName = "SnapTap";

    /// <summary>
    /// True when the scheduled task exists.
    /// </summary>
    public bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    /// <summary>
    /// Creates or removes the scheduled task. Returns false when Windows refused the change.
    /// </summary>
    public bool SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                RemoveRunEntry();

                // Deleting a task that does not exist is not an error for the user.
                RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
                return !IsEnabled();
            }

            var path = ExecutablePath();
            if (path is null)
            {
                return false;
            }

            var xmlPath = Path.Combine(Path.GetTempPath(), $"{TaskName}-task-{Guid.NewGuid():N}.xml");
            try
            {
                // Task Scheduler reads UTF-16 XML files, which is what the declaration says.
                File.WriteAllText(xmlPath, BuildTaskXml(path), Encoding.Unicode);
                return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F") == 0;
            }
            finally
            {
                try
                {
                    File.Delete(xmlPath);
                }
                catch
                {
                    // A leftover temporary file in the user's temp folder is harmless.
                }
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Brings the startup setup up to date when the application starts:
    /// removes the entry earlier builds left under the old name, moves a Run key entry
    /// to the scheduled task, and points an existing task at the current executable
    /// (the build may have been moved or rebuilt since it was created).
    /// </summary>
    public void Migrate()
    {
        RemoveLegacyEntry();

        if (HasRunEntry())
        {
            // The entry was enabled, so the task takes its place. The Run value is only
            // removed once the task exists, so a failed migration does not lose the setting.
            if (SetEnabled(true))
            {
                RemoveRunEntry();
            }
            return;
        }

        if (IsEnabled())
        {
            SetEnabled(true);
        }
    }

    /// <summary>
    /// Removes the entry an earlier build wrote under the old name. That entry points
    /// at an executable that no longer exists, so leaving it would make Windows report
    /// an error at every sign in.
    /// </summary>
    private static void RemoveLegacyEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(LegacyValueName) is not null)
            {
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Nothing to clean up if the key cannot be opened.
        }
    }

    private static bool HasRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(RunValueName) is not null)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Nothing to remove if the key cannot be opened.
        }
    }

    /// <summary>
    /// The task definition. The defaults of Task Scheduler stop a task after 72 hours
    /// and forbid it on battery power, which would switch the engine off for a tray
    /// application that is meant to run for as long as the session lasts, so both are
    /// set explicitly.
    /// </summary>
    private static string BuildTaskXml(string executable) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Starts Hooking when you sign in to Windows.</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <Delay>PT5S</Delay>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(executable)}</Command>
              <Arguments>--tray</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    /// <summary>
    /// Runs schtasks.exe and returns its exit code, or -1 when it could not be started.
    /// </summary>
    private static int RunSchtasks(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return -1;
            }

            process.WaitForExit(15000);
            return process.HasExited ? process.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Path of the running executable.
    ///
    /// Taken from the process rather than from the assembly location: a single file
    /// deployment reports an empty assembly path, and the process entry point is the
    /// one Windows will actually run.
    /// </summary>
    private static string? ExecutablePath()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}

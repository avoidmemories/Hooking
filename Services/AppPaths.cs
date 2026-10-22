using System.IO;

namespace Hooking.Services;

/// <summary>
/// Owns the application's persistent-data paths. User settings live under
/// %APPDATA%\Hooking; installed program files never do.
/// </summary>
public sealed class AppPaths
{
    /// <summary>Folder used by earlier builds, before the rename to Hooking.</summary>
    private const string LegacyFolderName = "SnapTap";

    public string RootDirectory { get; }
    public string SettingsFile { get; }
    /// <summary>
    /// The worker ships next to the application executable. Keeping it in the
    /// install/publish directory makes it part of the program being removed,
    /// rather than an executable left behind in the user's profile.
    /// </summary>
    public string WorkerExecutable { get; }

    public AppPaths()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        RootDirectory = Path.Combine(appData, "Hooking");
        SettingsFile = Path.Combine(RootDirectory, "settings.json");
        WorkerExecutable = Path.Combine(AppContext.BaseDirectory, "HookingWorker.exe");

        Directory.CreateDirectory(RootDirectory);

        MigrateLegacySettings(Path.Combine(appData, LegacyFolderName, "settings.json"));
    }

    /// <summary>
    /// Carries the profiles over from the previous folder name, so the rename does not
    /// look like a loss of every game. Only runs when the new file does not exist yet.
    /// </summary>
    private void MigrateLegacySettings(string legacySettings)
    {
        try
        {
            if (!File.Exists(SettingsFile) && File.Exists(legacySettings))
            {
                File.Copy(legacySettings, SettingsFile);
            }
        }
        catch
        {
            // Without the copy the app simply starts with empty settings.
        }
    }

}

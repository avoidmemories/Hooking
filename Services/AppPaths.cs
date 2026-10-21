using System.IO;
using System.Reflection;

namespace Hooking.Services;

/// <summary>
/// Owns every path the application touches. Everything lives under
/// %APPDATA%\Hooking so the app stays portable and self-contained.
/// </summary>
public sealed class AppPaths
{
    /// <summary>Folder used by earlier builds, before the rename to Hooking.</summary>
    private const string LegacyFolderName = "SnapTap";

    public string RootDirectory { get; }
    public string SettingsFile { get; }
    public string RuntimeDirectory { get; }

    public AppPaths()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        RootDirectory = Path.Combine(appData, "Hooking");
        SettingsFile = Path.Combine(RootDirectory, "settings.json");
        RuntimeDirectory = Path.Combine(RootDirectory, "runtime");

        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(RuntimeDirectory);

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

    /// <summary>
    /// Unpacks the embedded worker next to the settings so the executable on disk
    /// always matches the build the UI was compiled from.
    /// </summary>
    public string ExtractWorker()
    {
        var target = Path.Combine(RuntimeDirectory, "HookingWorker.exe");
        var assembly = Assembly.GetExecutingAssembly();

        using var stream = assembly.GetManifestResourceStream("HookingWorker.exe");
        if (stream is null)
        {
            return target;
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var payload = memory.ToArray();

        // Compare the payload rather than timestamps. A timestamp comparison
        // silently kept a stale worker on disk whenever the file happened to be
        // newer than the assembly, which meant a rebuilt engine was never picked
        // up — the interface and the worker drifted apart.
        if (File.Exists(target) && IsSameContent(target, payload))
        {
            return target;
        }

        var temporary = target + ".tmp";
        File.WriteAllBytes(temporary, payload);
        File.Move(temporary, target, overwrite: true);
        return target;
    }

    private static bool IsSameContent(string path, byte[] payload)
    {
        try
        {
            var existing = new FileInfo(path);
            if (existing.Length != payload.Length)
            {
                return false;
            }

            return File.ReadAllBytes(path).AsSpan().SequenceEqual(payload);
        }
        catch
        {
            return false;
        }
    }
}

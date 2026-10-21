using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hooking.Models;

namespace Hooking.Services;

public sealed class SettingsStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Written as names so the file stays readable and can be edited by hand.
        // The custom converter also tolerates a value this build does not know,
        // which is what keeps a file from an earlier build usable.
        Converters = { new EngineModeConverter() }
    };

    public AppSettings Load()
    {
        SuppressNextSave = false;
        LoadError = null;

        if (!File.Exists(paths.SettingsFile))
        {
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(paths.SettingsFile), Options);
            if (settings is null)
            {
                return new AppSettings();
            }

            Normalize(settings);
            return settings;
        }
        catch (Exception exception)
        {
            // Never lose the user's configuration silently. A malformed file used
            // to return defaults with no trace, which looked exactly like "all my
            // settings reset themselves". The bad file is kept for inspection.
            var backup = paths.SettingsFile + ".invalid";
            try
            {
                File.Copy(paths.SettingsFile, backup, overwrite: true);
            }
            catch
            {
            }

            // The old file stays on disk untouched, so a parse failure does not
            // wipe the user's profiles when the interface next saves.
            SuppressNextSave = true;

            LoadError = $"settings.json could not be read ({exception.Message}). A copy was kept as settings.json.invalid.";
            return new AppSettings();
        }
    }

    /// <summary>
    /// Set when the last load failed. The interface must not overwrite the file
    /// in that case, or a single unreadable key would erase every profile.
    /// </summary>
    public bool SuppressNextSave { get; private set; }

    /// <summary>
    /// Set when the last <see cref="Load"/> could not parse the file, so the
    /// interface can say so instead of appearing to forget everything.
    /// </summary>
    public string? LoadError { get; private set; }

    public void Save(AppSettings settings)
    {
        if (SuppressNextSave)
        {
            // The file on disk could not be read, so the in-memory state is not a
            // faithful superset of it. Writing now would destroy whatever the file
            // still holds. One deliberate edit clears the flag.
            return;
        }

        try
        {
            File.WriteAllText(paths.SettingsFile, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // A failed save is retried on the next edit, so nothing is gained by stopping.
        }
    }

    /// <summary>
    /// Allows saving again after the user makes a deliberate change.
    /// </summary>
    public void ClearSuppression() => SuppressNextSave = false;

    /// <summary>
    /// Guards edited or half written settings files.
    /// </summary>
    private static void Normalize(AppSettings settings)
    {
        settings.Presets ??= [];
        settings.Profiles ??= [];

        foreach (var profile in settings.Profiles)
        {
            profile.Presets ??= [];
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                profile.Id = Guid.NewGuid().ToString("N");
            }

            // Migration: a profile written when key pairs were shared globally has
            // an empty list and the old flag set. Give it its own copy of the
            // shared list, otherwise the game would silently lose its pairs when
            // the flag stops being honoured.
            if (profile.Presets.Count == 0 && profile.UseGlobalPresets && settings.Presets.Count > 0)
            {
                profile.Presets = settings.Presets
                    .Select(x => new KeyPairPreset(x.FirstKey, x.SecondKey))
                    .ToList();
            }

            // The flag is no longer meaningful: from here on every profile owns
            // its pairs.
            profile.UseGlobalPresets = false;

            // A method this build does not have is replaced with a supported one,
            // so the file written back never carries a value that would be unknown
            // to the next load.
            if (!Enum.IsDefined(profile.Mode))
            {
                profile.Mode = EngineMode.Release;
            }
        }
    }
}
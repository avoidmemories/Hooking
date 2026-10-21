// Isolated check of the settings pipeline. Reads the file exactly the way the
// interface does and reports both the raw bytes and the parsed result, so a
// disagreement between "what is on disk" and "what the app loaded" can be
// pinpointed instead of guessed at.
//
//   Hooking.exe --settings-probe <report path>

using System.IO;
using System.Text;
using System.Text.Json;
using Hooking.Models;
using Hooking.Services;

namespace Hooking;

internal static class SettingsProbe
{
    public static int Run(string reportPath)
    {
        var report = new StringBuilder();
        var paths = new AppPaths();
        var store = new SettingsStore(paths);

        report.AppendLine($"root            {paths.RootDirectory}");
        report.AppendLine($"settings file   {paths.SettingsFile}");
        report.AppendLine($"file exists     {File.Exists(paths.SettingsFile)}");

        if (File.Exists(paths.SettingsFile))
        {
            var raw = File.ReadAllText(paths.SettingsFile);
            report.AppendLine($"file length     {raw.Length}");
            report.AppendLine($"raw content:");
            report.AppendLine(raw);
        }

        // Straight deserialization, to separate a model problem from anything the
        // store might do around it.
        try
        {
            var direct = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(paths.SettingsFile),
                new JsonSerializerOptions
                {
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(allowIntegerValues: true) }
                });

            report.AppendLine($"direct parse    enabled={direct?.HookingEnabled} profiles={direct?.Profiles.Count} anyPairs={direct?.Presets.Count}");
        }
        catch (Exception exception)
        {
            report.AppendLine($"direct parse    FAILED: {exception.GetType().Name}: {exception.Message}");
        }

        var loaded = store.Load();
        report.AppendLine($"store.Load      enabled={loaded.HookingEnabled} profiles={loaded.Profiles.Count} anyPairs={loaded.Presets.Count}");
        report.AppendLine($"store.LoadError {store.LoadError ?? "(none)"}");
        report.AppendLine($"suppress save   {store.SuppressNextSave}");

        report.AppendLine();
        report.AppendLine("key resolution  (name -> virtual key -> scan code)");

        // The keys that were previously impossible or easy to get wrong. A name
        // that yields scan code 0 cannot be bound at all, so this is the fastest
        // way to see whether a key is actually usable.
        string[] names =
        [
            "Tab", "Space", "Enter", "Escape", "Backspace", "CapsLock",
            "Up", "Down", "Left", "Right", "Insert", "Delete", "Home", "End",
            "PageUp", "PageDown",
            "Q", "W", "E", "R", "A", "S", "D", "F", "1", "5", "0",
            "F1", "F5", "F12", "F24",
            "NumPad0", "NumPad5", "NumPad9",
            "=", "-", ",", ".", "/", ";", "'", "[", "]", "\\", "`",
            "LShift", "RShift", "LCtrl", "RCtrl", "LAlt", "RAlt"
        ];

        // A key id that repeats another one is a collision: those two names would
        // bind the same physical key.
        var seen = new Dictionary<int, string>();

        foreach (var name in names)
        {
            var keyId = KeyNames.ToKeyId(name);
            if (keyId == 0)
            {
                report.AppendLine($"  {name,-12} UNRESOLVED");
                continue;
            }

            var collision = seen.TryGetValue(keyId, out var other) ? $"  COLLIDES WITH {other}" : string.Empty;
            seen.TryAdd(keyId, name);

            report.AppendLine(
                $"  {name,-12} keyId={keyId} (scan={keyId & 0xFF}, extended={(keyId & KeyNames.ExtendedFlag) != 0}){collision}");
        }

        File.WriteAllText(reportPath, report.ToString());
        return 0;
    }
}
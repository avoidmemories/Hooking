using System.Text.Json.Serialization;

namespace SnapHook.Models;

/// <summary>
/// One key pairing. When <see cref="FirstKey"/> and <see cref="SecondKey"/> are held
/// at the same time the later press wins, which is what snap tap does.
/// </summary>
public sealed record KeyPairPreset(string FirstKey, string SecondKey)
{
    public override string ToString() => $"{FirstKey} : {SecondKey}";
}

/// <summary>
/// A game the engine is armed for. A profile carries its own key presets, so
/// counter-strike style movement (WASD) and driving games (arrows) can coexist.
/// </summary>
public sealed class GameProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New Profile";
    public string ProcessName { get; set; } = string.Empty;
    public string? ExecutablePath { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How events reach this game. Per profile, because the right choice depends
    /// on what the game reads.
    /// </summary>
    public EngineMode Mode { get; set; } = EngineMode.Release;

    /// <summary>
    /// Which key pairs apply to this game.
    ///
    /// Every game keeps its own list: a movement shooter and a driving game do
    /// not share pairs, so profiles do not inherit a global set. The flag exists
    /// only so an older settings file can still be read.
    /// </summary>
    public bool UseGlobalPresets { get; set; }

    public List<KeyPairPreset> Presets { get; set; } = [];

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? ProcessName : Title;

    public override string ToString() => DisplayName;
}

public sealed class AppSettings
{
    /// <summary>
    /// Master switch. The worker process is alive exactly when this is true.
    /// </summary>
    [JsonPropertyName("SnapHookEnabled")]
    public bool SnapHookEnabled { get; set; }

    /// <summary>
    /// Pairs kept from the retired "Apply to any application" mode.
    ///
    /// Left in place so an existing settings file still loads: removing the field would
    /// make the whole file unreadable for the previous build, and the pairs are useful
    /// as a source to copy from when setting up a new game.
    /// </summary>
    public List<KeyPairPreset> Presets { get; set; } =
    [
        new("A", "D"),
        new("W", "S"),
        new("Q", "E")
    ];

    public List<GameProfile> Profiles { get; set; } = [];

    /// <summary>
    /// When true the application is registered to launch with Windows.
    /// </summary>
    public bool StartWithWindows { get; set; }

    public string SelectedProfileId { get; set; } = string.Empty;

    /// <summary>
    /// Returns every profile that should be armed. Each profile carries its own
    /// operation and its own key pairs.
    /// </summary>
    public IEnumerable<GameProfile> ActiveTargets()
    {
        foreach (var profile in Profiles)
        {
            if (!profile.Enabled || string.IsNullOrWhiteSpace(profile.ProcessName))
            {
                continue;
            }

            if (profile.Presets.Count > 0)
            {
                yield return profile;
            }
        }
    }
}

public sealed record OpenAppInfo(string ProcessName, string? ExecutablePath, string? WindowTitle, bool IsRunning)
{
    public string DisplayName => ProcessName;

    public string SelectionKey => ProcessName;
}

/// <summary>
/// How the engine feeds a game. Games differ in what they actually read, so this
/// is chosen per profile.
/// </summary>
public enum EngineMode
{
    /// <summary>
    /// The physical key is never swallowed; the engine only adds a release when
    /// opposing directions overlap. Correct for games that read the input stream
    /// (raw input, DirectInput, window messages). Movement can never break,
    /// because the native event still arrives.
    /// </summary>
    Release,

    /// <summary>
    /// The physical press is swallowed and replaced by an injected one, as in the
    /// original null-movement implementation.
    /// </summary>
    Blocking,
}

/// <summary>
/// Human readable names for <see cref="EngineMode"/>, used in the interface and
/// on the worker command line.
/// </summary>
public static class EngineModes
{
    public static string ToToken(EngineMode mode) => mode switch
    {
        EngineMode.Blocking => "blocking",
        _ => "release"
    };

    public static string ToLabel(EngineMode mode) => mode switch
    {
        EngineMode.Blocking => "Blocking",
        _ => "Release"
    };

    public static EngineMode Parse(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        "blocking" => EngineMode.Blocking,
        _ => EngineMode.Release
    };
}

public enum WorkerState
{
    Stopped,
    Running,
    Failed
}

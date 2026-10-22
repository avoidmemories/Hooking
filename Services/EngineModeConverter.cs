using System.Text.Json;
using System.Text.Json.Serialization;
using SnapHook.Models;

namespace SnapHook.Services;

/// <summary>
/// Reads and writes <see cref="EngineMode"/> as a name, and never fails.
///
/// A settings file is read by a build that may not know every value in it: the
/// Polling and Messages modes existed for a while and were then removed, so a file
/// written back then contains "Polling", which a later build cannot parse. The
/// default converter throws on that, and because the whole file is parsed in one
/// call, one unknown value discarded every profile the user had — which is exactly
/// what happened.
///
/// An unknown name now falls back to the safest mode instead of failing, so a file
/// from any earlier build still loads with its games intact.
/// </summary>
public sealed class EngineModeConverter : JsonConverter<EngineMode>
{
    public override EngineMode Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        // Numbers are accepted too, for a file written by the very first build,
        // which stored the enum as an integer.
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
        {
            return Enum.IsDefined(typeof(EngineMode), number) ? (EngineMode)number : EngineMode.Release;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            return EngineMode.Release;
        }

        var name = reader.GetString();

        // An unknown name, such as a retired mode, degrades rather than throwing.
        return EngineModes.Parse(name);
    }

    public override void Write(Utf8JsonWriter writer, EngineMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(EngineModes.ToToken(value));
}

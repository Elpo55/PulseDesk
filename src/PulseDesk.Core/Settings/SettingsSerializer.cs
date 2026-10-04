using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PulseDesk.Core.Settings;

/// <summary>
/// Converts settings to and from JSON. Uses source-generated serialization (fast startup, trim-safe).
/// Unknown properties are ignored and missing ones keep their default value, so files written by
/// older or newer versions of PulseDesk still load.
/// </summary>
public static class SettingsSerializer
{
    public static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(SettingsValidator.Normalize(settings), SettingsJsonContext.Default.AppSettings);

    /// <summary>Parses and validates a settings document.</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="settings">The normalized settings, when parsing succeeded.</param>
    /// <param name="error">Why parsing failed, when it did.</param>
    public static bool TryDeserialize(
        string json,
        [NotNullWhen(true)] out AppSettings? settings,
        [NotNullWhen(false)] out string? error)
    {
        settings = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "The settings document is empty.";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);
            if (parsed is null)
            {
                error = "The settings document is null.";
                return false;
            }

            settings = SettingsValidator.Normalize(parsed);
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Sysora.Core.Settings;

/// <summary>
/// Converts settings to and from JSON. Uses source-generated serialization (fast startup, trim-safe).
/// Unknown properties are ignored and missing ones keep their default value, so files written by
/// older or newer versions of Sysora still load.
/// </summary>
/// <remarks>
/// Settings are immutable records with <c>init</c> properties, which the serializer sets all at once: a property absent
/// from a section that is present would get <c>default(T)</c> (false, 0, the first enum value) instead of its declared
/// default. The stored document is therefore laid over the serialized defaults before it is read, so a setting added by
/// a newer version always starts at its intended default.
/// </remarks>
public static class SettingsSerializer
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

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
            if (JsonNode.Parse(json, documentOptions: ReadOptions) is not JsonObject stored)
            {
                error = "The settings document is not a JSON object.";
                return false;
            }

            var document = JsonNode.Parse(Serialize(AppSettings.Default))!.AsObject();
            Merge(document, stored);
            var parsed = document.Deserialize(SettingsJsonContext.Default.AppSettings);
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

    /// <summary>Copies <paramref name="source"/> over <paramref name="target"/>, section by section (stored values win).</summary>
    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (name, value) in source.ToArray())
        {
            if (value is JsonObject section && target[name] is JsonObject defaults)
            {
                Merge(defaults, section);
                continue;
            }

            // A node belongs to one parent: detach it before moving it.
            source.Remove(name);
            target[name] = value;
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

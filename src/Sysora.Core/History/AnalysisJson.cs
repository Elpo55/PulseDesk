using System.Text.Json;
using System.Text.Json.Serialization;
using Sysora.Core.Alerts;
using Sysora.Core.Changes;
using Sysora.Core.Gaming;

namespace Sysora.Core.History;

/// <summary>
/// JSON form of the analysis records kept in the local history (alerts, baselines, detected changes).
/// Source-generated, so it is fast and trim-safe. A document that cannot be read (written by another version,
/// damaged) is skipped rather than failing the whole history.
/// </summary>
public static class AnalysisJson
{
    public static string Serialize(Alert alert) => JsonSerializer.Serialize(alert, AnalysisJsonContext.Default.Alert);

    public static Alert? DeserializeAlert(string json) => TryDeserialize(json, AnalysisJsonContext.Default.Alert);

    public static string Serialize(SystemBaseline baseline) => JsonSerializer.Serialize(baseline, AnalysisJsonContext.Default.SystemBaseline);

    public static SystemBaseline? DeserializeBaseline(string json) => TryDeserialize(json, AnalysisJsonContext.Default.SystemBaseline);

    public static string Serialize(DetectedChange change) => JsonSerializer.Serialize(change, AnalysisJsonContext.Default.DetectedChange);

    public static DetectedChange? DeserializeChange(string json) => TryDeserialize(json, AnalysisJsonContext.Default.DetectedChange);

    public static string Serialize(GameSession session) => JsonSerializer.Serialize(session, AnalysisJsonContext.Default.GameSession);

    public static GameSession? DeserializeGameSession(string json) => TryDeserialize(json, AnalysisJsonContext.Default.GameSession);

    private static T? TryDeserialize<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(json, type);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Alert))]
[JsonSerializable(typeof(SystemBaseline))]
[JsonSerializable(typeof(DetectedChange))]
[JsonSerializable(typeof(GameSession))]
internal sealed partial class AnalysisJsonContext : JsonSerializerContext;

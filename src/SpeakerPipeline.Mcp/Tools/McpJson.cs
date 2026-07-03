using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpeakerPipeline.Mcp.Tools;

/// <summary>
/// Shared serialization for MCP tool responses. Reads return compact JSON so
/// the calling model gets structured data; writes return a short confirmation.
/// </summary>
internal static class McpJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Matterhorn.Configuration;

/// <summary>
/// The single JSON shape shared by the MQTT and REST projections so the two surfaces are
/// byte-identical and Z2M-compatible: snake_case property names, null fields
/// omitted (e.g. an <c>exposes</c> entry only carries the keys that apply to it).
/// </summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions SnakeCase = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

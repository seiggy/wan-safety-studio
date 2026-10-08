using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoSafetyStudio.Application;

/// <summary>JSON used for storage records and the HTTP API: camelCase, nulls written, case-insensitive on read.</summary>
public static class Json
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false,
        };
        return options;
    }

    public static byte[] ToBytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T? From<T>(byte[] data) => JsonSerializer.Deserialize<T>(data, Options);
}

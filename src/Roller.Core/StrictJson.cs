using System.Text.Json;
using System.Text.Json.Serialization;

namespace Roller.Core;

/// <summary>JSON settings for config files and API input: missing, null or unknown fields are errors.</summary>
public static class StrictJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Reads one of the files under config/, embedded in this assembly.</summary>
    public static T ReadConfig<T>(string fileName)
    {
        using var stream = typeof(StrictJson).Assembly.GetManifestResourceStream(fileName)
            ?? throw new InvalidOperationException($"Embedded config {fileName} not found");

        return JsonSerializer.Deserialize<T>(stream, Options)
            ?? throw new InvalidDataException($"Embedded config {fileName} is empty");
    }
}

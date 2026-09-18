using System.Text.Json.Serialization;

namespace Kite.Config;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(Presets))]
[JsonSerializable(typeof(KiteAuth))]
[JsonSerializable(typeof(KiteState))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;
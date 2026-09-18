using System.Text.Json.Serialization;

namespace Kite.Sessions;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SessionLine))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;
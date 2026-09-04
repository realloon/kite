using System.Text.Json.Serialization;
using Kite.Agent;
using Kite.Sessions;
using Kite.Tools;

namespace Kite.Configuration;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ResponsesAgent.ResponsesRequest))]
[JsonSerializable(typeof(ToolDefinition))]
[JsonSerializable(typeof(KiteConfig))]
[JsonSerializable(typeof(KiteAuth))]
[JsonSerializable(typeof(KiteState))]
[JsonSerializable(typeof(Session))]
[JsonSerializable(typeof(SessionLine))]
internal sealed partial class KiteJsonContext : JsonSerializerContext;
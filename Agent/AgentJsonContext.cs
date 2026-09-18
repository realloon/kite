using System.Text.Json.Serialization;

namespace Kite.Agent;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ResponsesAgent.ResponsesRequest))]
[JsonSerializable(typeof(CompletionsAgent.CompletionsRequest))]
[JsonSerializable(typeof(ToolDefinition))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;
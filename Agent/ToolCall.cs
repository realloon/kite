using System.Text.Json;

namespace Kite.Agent;

/// <summary>A function call the model asked us to execute.</summary>
public sealed record ToolCall(string Id, string Name, string Arguments);

/// <summary>Function tool declaration sent to the model.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonElement Parameters) {
    public string Type { get; } = "function";
}
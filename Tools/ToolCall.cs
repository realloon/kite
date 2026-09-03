using System.Text.Json;

namespace Kite.Tools;

public sealed record ToolCall(string Id, string Name, string Arguments);

public sealed record ToolDefinition(string Name, string Description, JsonElement Parameters) {
    public string Type { get; } = "function";
}
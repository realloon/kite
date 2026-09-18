using System.Text.Json;

namespace Kite.Agent;

internal sealed record ConversationMessage(
    string Role,
    string Content,
    string? Type = null,
    string? CallId = null,
    string? Name = null,
    string? Arguments = null) {
    public const string FunctionCallType = "function_call";
    public const string FunctionCallOutputType = "function_call_output";

    public static ConversationMessage User(string text) => new("user", text);

    public static ConversationMessage Assistant(string text) => new("assistant", text);

    public static ConversationMessage FunctionCall(ToolCall call) => new(
        string.Empty,
        string.Empty,
        FunctionCallType,
        call.Id,
        call.Name,
        call.Arguments);

    public static ConversationMessage FunctionCallOutput(string callId, string output) => new(
        string.Empty,
        output,
        FunctionCallOutputType,
        callId);
}

internal enum AgentEventKind {
    TextDelta,
    ReasoningDelta
}

internal readonly record struct AgentEvent(AgentEventKind Kind, string Text) {
    public static AgentEvent TextDelta(string text) => new(AgentEventKind.TextDelta, text);

    public static AgentEvent ReasoningDelta(string text) => new(AgentEventKind.ReasoningDelta, text);
}

internal sealed record AgentReply(int PromptTokens, int CompletionTokens, int CachedTokens = 0, int ContextTokens = 0) {
    public static readonly AgentReply Empty = new(0, 0);
}

internal sealed record ToolCall(string Id, string Name, string Arguments) {
    private const int PreviewLength = 120;

    public string Preview => FormatPreview(Name, Arguments);

    public static string FormatPreview(string name, string arguments) {
        var preview = arguments
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');
        if (preview.Length > PreviewLength) {
            preview = $"{preview[..(PreviewLength - 1)]}…";
        }

        return preview.Length == 0
            ? $"• {name}"
            : $"• {name} {preview}";
    }
}

internal sealed record ToolDefinition(string Name, string Description, JsonElement Parameters) {
    public string Type { get; } = "function";
}
using System.Text.Json;

namespace Kite.Agent;

internal abstract record ConversationMessage {
    public static UserMessage User(string text) => new(text);
    public static AssistantMessage Assistant(string text) => new(text);
    public static FunctionCallMessage FunctionCall(ToolCall call) => new(call.Id, call.Name, call.Arguments);
    public static FunctionCallOutputMessage FunctionCallOutput(string callId, string output) => new(callId, output);
}

internal sealed record UserMessage(string Content) : ConversationMessage;

internal sealed record AssistantMessage(string Content) : ConversationMessage;

internal sealed record FunctionCallMessage(string CallId, string Name, string Arguments) : ConversationMessage;

internal sealed record FunctionCallOutputMessage(string CallId, string Output) : ConversationMessage;

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
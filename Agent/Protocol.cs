using Kite.Tools;

namespace Kite.Agent;

public sealed record ConversationMessage(
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
        call.Arguments
    );

    public static ConversationMessage FunctionCallOutput(string callId, string output) => new(
        string.Empty,
        output,
        FunctionCallOutputType,
        callId
    );
}

public enum AgentEventKind {
    TextDelta,
    ReasoningDelta
}

public readonly record struct AgentEvent(AgentEventKind Kind, string Text) {
    public static AgentEvent TextDelta(string text) => new(AgentEventKind.TextDelta, text);

    public static AgentEvent ReasoningDelta(string text) => new(AgentEventKind.ReasoningDelta, text);
}

public sealed record AgentReply(int PromptTokens, int CompletionTokens, int CachedTokens = 0) {
    public static readonly AgentReply Empty = new(0, 0);
}
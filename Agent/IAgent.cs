namespace Kite.Agent;

public sealed record ConversationMessage(string Role, string Content) {
    public static ConversationMessage User(string text) => new("user", text);

    public static ConversationMessage Assistant(string text) => new("assistant", text);
}

public enum AgentEventKind {
    TextDelta,
    ReasoningDelta
}

public readonly record struct AgentEvent(AgentEventKind Kind, string Text) {
    public static AgentEvent TextDelta(string text) => new(AgentEventKind.TextDelta, text);

    public static AgentEvent ReasoningDelta(string text) => new(AgentEventKind.ReasoningDelta, text);
}

public sealed record AgentReply(string Text, int PromptTokens, int CompletionTokens) {
    public static readonly AgentReply Empty = new(string.Empty, 0, 0);
}

/// <summary>
/// Agent interface: given the full conversation, stream reply fragments.
/// The Responses API is stateless, so the caller must pass all history on every request.
/// </summary>
public interface IAgent {
    string ModelName { get; }

    string DisplayName { get; }

    Task<AgentReply> StreamReplyAsync(
        IReadOnlyList<ConversationMessage> conversation,
        Func<AgentEvent, Task> onEvent,
        CancellationToken cancellationToken);
}

namespace Kite.Agent;

/// <summary>One conversation message (text form of a Responses API input item).</summary>
public sealed record ConversationMessage(string Role, string Content) {
    public static ConversationMessage User(string text) => new("user", text);

    public static ConversationMessage Assistant(string text) => new("assistant", text);
}

/// <summary>One agent turn result: full reply text + real token usage.</summary>
public sealed record AgentReply(string Text, int PromptTokens, int CompletionTokens) {
    public static readonly AgentReply Empty = new(string.Empty, 0, 0);
}

/// <summary>
/// Agent interface: given the full conversation, stream reply fragments.
/// The Responses API is stateless, so the caller must pass all history on every request.
/// </summary>
public interface IAgent {
    /// <summary>Model id used for API requests.</summary>
    string ModelName { get; }

    /// <summary>Footer label (model · variant).</summary>
    string DisplayName { get; }

    Task<AgentReply> StreamReplyAsync(
        IReadOnlyList<ConversationMessage> conversation,
        Func<string, Task> onChunk,
        CancellationToken cancellationToken);
}
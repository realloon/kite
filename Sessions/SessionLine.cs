using Kite.Agent;

namespace Kite.Sessions;

internal sealed class SessionLine {
    public string Type { get; init; } = string.Empty;

    public string? Id { get; init; }

    public string? Workspace { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public decimal Cost { get; set; }

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    public int CachedTokens { get; set; }

    public int LastPromptTokens { get; set; }

    public List<ConversationMessage> Messages { get; init; } = [];
}
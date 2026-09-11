using Kite.Agent;

namespace Kite.Sessions;

public sealed class Session {
    public string Id { get; init; } = string.Empty;

    public string Workspace { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public decimal Cost { get; set; }

    public List<ConversationMessage> Messages { get; init; } = [];
}
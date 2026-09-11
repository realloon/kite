using Kite.Agent;

namespace Kite.Sessions;

internal sealed class SessionLine {
    public string Type { get; set; } = string.Empty;

    public string? Id { get; set; }

    public string? Workspace { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public decimal Cost { get; set; }

    public List<ConversationMessage> Messages { get; set; } = [];
}
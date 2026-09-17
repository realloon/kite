using System.Text.Json.Serialization;
using Kite.Agent;

namespace Kite.Sessions;

internal sealed class SessionLine {
    public string Type { get; set; } = string.Empty;

    public string? Id { get; set; }

    public string? Workspace { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public decimal Cost { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int PromptTokens { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CompletionTokens { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CachedTokens { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int LastPromptTokens { get; set; }

    public List<ConversationMessage>? Messages { get; set; }
}
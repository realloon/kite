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

    public List<MessageDto>? Messages { get; set; }
}

internal sealed class MessageDto {
    private const string FunctionCallType = "function_call";
    private const string FunctionCallOutputType = "function_call_output";

    public string? Role { get; set; }
    public string? Content { get; set; }
    public string? Type { get; set; }
    public string? CallId { get; set; }
    public string? Name { get; set; }
    public string? Arguments { get; set; }

    public static MessageDto FromMessage(ConversationMessage message) => message switch {
        UserMessage user => new MessageDto { Role = "user", Content = user.Content },
        AssistantMessage assistant => new MessageDto { Role = "assistant", Content = assistant.Content },
        FunctionCallMessage call => new MessageDto {
            Role = string.Empty,
            Content = string.Empty,
            Type = FunctionCallType,
            CallId = call.CallId,
            Name = call.Name,
            Arguments = call.Arguments
        },
        FunctionCallOutputMessage output => new MessageDto {
            Role = string.Empty,
            Content = output.Output,
            Type = FunctionCallOutputType,
            CallId = output.CallId
        },
        _ => throw new InvalidOperationException($"Unknown conversation message: {message.GetType().Name}")
    };

    public ConversationMessage ToMessage(string sessionId) {
        if (Role is null || Content is null) {
            throw new InvalidOperationException($"Session '{sessionId}' contains an invalid message");
        }

        return Type switch {
            null when Role == "user" && CallId is null && Name is null && Arguments is null =>
                new UserMessage(Content),
            null when Role == "assistant" && CallId is null && Name is null && Arguments is null =>
                new AssistantMessage(Content),
            null => throw new InvalidOperationException($"Session '{sessionId}' contains an invalid message"),
            FunctionCallType when Role.Length == 0 && Content.Length == 0 &&
                                  !string.IsNullOrEmpty(CallId) && !string.IsNullOrEmpty(Name) &&
                                  Arguments is not null =>
                new FunctionCallMessage(CallId, Name, Arguments),
            FunctionCallType =>
                throw new InvalidOperationException($"Session '{sessionId}' contains an invalid function call"),
            FunctionCallOutputType when Role.Length == 0 && !string.IsNullOrEmpty(CallId) =>
                new FunctionCallOutputMessage(CallId, Content),
            FunctionCallOutputType =>
                throw new InvalidOperationException($"Session '{sessionId}' contains an invalid function call output"),
            _ => throw new InvalidOperationException(
                $"Session '{sessionId}' contains an unknown message type: {Type}")
        };
    }
}
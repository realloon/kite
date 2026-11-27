using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kite.Config;

namespace Kite.Agent;

internal sealed class ResponsesAgent(
    string apiKey,
    ModelPreset model,
    string? variant,
    string instructions,
    IReadOnlyList<ToolDefinition> localTools)
    : AgentClient(apiKey, model, "/responses", variant, instructions, localTools) {
    public override Task<AgentReply> StreamReplyAsync(
        IReadOnlyList<ConversationMessage> conversation,
        string sessionId,
        Func<AgentEvent, Task> onEvent,
        Func<IReadOnlyList<ToolCall>, CancellationToken, Task<IReadOnlyList<string>>>? executeToolCalls,
        CancellationToken ct) =>
        RunToolLoopAsync(
            conversation.Select(ToInputItem).ToList(),
            items => StreamRoundAsync(items, sessionId, onEvent, ct),
            AppendToolRound,
            executeToolCalls,
            ct);

    private static void AppendToolRound(List<InputItem> items, RoundResult round, IReadOnlyList<string> outputs) {
        if (round.Text.Length > 0) {
            items.Add(new InputItem { Role = "assistant", Content = round.Text });
        }

        items.AddRange(round.Calls.Select(call => new InputItem {
            Type = "function_call",
            CallId = call.Id,
            Name = call.Name,
            Arguments = call.Arguments
        }));

        items.AddRange(round.Calls.Select((call, index) => new InputItem {
            Type = "function_call_output",
            CallId = call.Id,
            Output = outputs[index]
        }));
    }

    private async Task<RoundResult> StreamRoundAsync(
        List<InputItem> items,
        string sessionId,
        Func<AgentEvent, Task> onEvent,
        CancellationToken ct) {
        var request = new ResponsesRequest {
            Model = Model.Id,
            Input = items,
            Instructions = Instructions,
            Stream = true,
            Reasoning = ReasoningEffort is null ? null : new ReasoningRequest { Effort = ReasoningEffort },
            MaxOutputTokens = Model.Limit.Output,
            Tools = BuildTools()
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(request, AgentJsonContext.Default.ResponsesRequest);
        using var httpRequest = CreateRequest(json, sessionId);
        using var response = await SendAsync(httpRequest, ct);

        var text = new StringBuilder();
        var promptTokens = 0;
        var completionTokens = 0;
        var cachedTokens = 0;
        var calls = new List<ToolCall>();
        string? failure = null;

        var (terminalEventSeen, interrupted) = await ReadSseAsync(response, "Response", async root => {
            switch (ReadString(root, "type")) {
                case "response.output_text.delta": {
                    if (ReadString(root, "delta") is { Length: > 0 } delta) {
                        text.Append(delta);
                        await onEvent(AgentEvent.TextDelta(delta));
                    }

                    return false;
                }
                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta": {
                    if (ReadString(root, "delta") is { Length: > 0 } delta) {
                        await onEvent(AgentEvent.ReasoningDelta(delta));
                    }

                    return false;
                }
                case "response.completed":
                case "response.incomplete": {
                    var container = root.TryGetProperty("response", out var res) ? res : root;
                    if (container.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object) {
                        (promptTokens, completionTokens, cachedTokens) = ReadUsage(usage);
                    }

                    calls = ReadFunctionCalls(root);
                    return true;
                }
                case "response.failed":
                    failure = TryReadFailureMessage(root) ?? "Unknown error";
                    return true;
                default:
                    return false;
            }
        }, ct);

        if (failure is not null) {
            throw new InvalidOperationException(failure);
        }

        if (!terminalEventSeen && !interrupted) {
            throw new InvalidOperationException("Response stream ended before response.completed");
        }

        return new RoundResult(text.ToString(), calls, promptTokens, completionTokens, cachedTokens, interrupted);
    }

    // Advertised on every request (including compaction) to preserve prefix cache.
    private List<JsonElement> BuildTools() => [
        .. Model.Tools,
        .. LocalTools.Select(tool => JsonSerializer.SerializeToElement(tool, AgentJsonContext.Default.ToolDefinition))
    ];

    private static InputItem ToInputItem(ConversationMessage message) => message switch {
        UserMessage user => new InputItem { Role = "user", Content = user.Content },
        AssistantMessage assistant => new InputItem { Role = "assistant", Content = assistant.Content },
        FunctionCallMessage call => new InputItem {
            Type = "function_call",
            CallId = call.CallId,
            Name = call.Name,
            Arguments = call.Arguments
        },
        FunctionCallOutputMessage output => new InputItem {
            Type = "function_call_output",
            CallId = output.CallId,
            Output = output.Output
        },
        _ => throw new InvalidOperationException($"Unknown conversation message: {message.GetType().Name}")
    };

    private static List<ToolCall> ReadFunctionCalls(JsonElement root) {
        if (!root.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("output", out var output)) {
            return [];
        }

        return [
            .. output.EnumerateArray()
                .Where(item => ReadString(item, "type") == "function_call")
                .Select(item => new ToolCall(
                    ReadString(item, "call_id") ?? ReadString(item, "id") ?? string.Empty,
                    ReadString(item, "name") ?? string.Empty,
                    ReadString(item, "arguments") ?? string.Empty))
        ];
    }

    private static string? TryReadFailureMessage(JsonElement root) {
        if (root.TryGetProperty("response", out var response) &&
            response.TryGetProperty("error", out var error) &&
            error.TryGetProperty("message", out var message)) {
            return message.GetString();
        }

        return null;
    }

    internal sealed class ResponsesRequest {
        public required string Model { get; init; }
        public required List<InputItem> Input { get; init; }
        public required string Instructions { get; init; }
        public bool Stream { get; init; }
        public ReasoningRequest? Reasoning { get; init; }

        [JsonPropertyName("max_output_tokens")]
        public int MaxOutputTokens { get; init; }

        public required List<JsonElement> Tools { get; init; }
    }

    internal sealed class InputItem {
        public string Type { get; init; } = "message";
        public string? Role { get; init; }
        public string? Content { get; init; }

        [JsonPropertyName("call_id")]
        public string? CallId { get; init; }

        public string? Name { get; init; }
        public string? Arguments { get; init; }
        public string? Output { get; init; }
    }

    internal sealed class ReasoningRequest {
        public required string Effort { get; init; }
    }
}
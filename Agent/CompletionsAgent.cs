using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kite.Config;

namespace Kite.Agent;

internal sealed class CompletionsAgent(
    string apiKey,
    ModelPreset model,
    string? variant,
    string instructions,
    IReadOnlyList<ToolDefinition> localTools)
    : AgentClient(apiKey, model, "/chat/completions", variant, instructions, localTools) {
    public override Task<AgentReply> StreamReplyAsync(
        IReadOnlyList<ConversationMessage> conversation,
        string sessionId,
        Func<AgentEvent, Task> onEvent,
        Func<IReadOnlyList<ToolCall>, CancellationToken, Task<IReadOnlyList<string>>>? executeToolCalls,
        CancellationToken ct) =>
        RunToolLoopAsync(
            BuildMessages(conversation),
            messages => StreamRoundAsync(messages, sessionId, onEvent, ct),
            AppendToolRound,
            executeToolCalls,
            ct);

    private static void AppendToolRound(List<ChatMessage> messages, RoundResult round, IReadOnlyList<string> outputs) {
        messages.Add(new ChatMessage {
            Role = "assistant",
            Content = round.Text.Length > 0 ? round.Text : null,
            ToolCalls = [
                .. round.Calls.Select(call => new ToolCallDto {
                    Id = call.Id,
                    Function = new FunctionDto {
                        Name = call.Name,
                        Arguments = call.Arguments
                    }
                })
            ]
        });

        for (var i = 0; i < round.Calls.Count; i++) {
            messages.Add(new ChatMessage {
                Role = "tool",
                ToolCallId = round.Calls[i].Id,
                Content = outputs[i]
            });
        }
    }

    private async Task<RoundResult> StreamRoundAsync(
        List<ChatMessage> messages,
        string sessionId,
        Func<AgentEvent, Task> onEvent,
        CancellationToken ct) {
        var request = new CompletionsRequest {
            Model = Model.Id,
            Messages = messages,
            Stream = true,
            MaxTokens = Model.Limit.Output,
            ReasoningEffort = ReasoningEffort,
            Tools = BuildTools()
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(request, AgentJsonContext.Default.CompletionsRequest);
        using var httpRequest = CreateRequest(json, sessionId);
        using var response = await SendAsync(httpRequest, ct);

        var text = new StringBuilder();
        var toolCallAccumulators = new SortedDictionary<int, ToolCallAccumulator>();
        var promptTokens = 0;
        var completionTokens = 0;
        var cachedTokens = 0;

        var (done, interrupted) = await ReadSseAsync(response, "Completion", async root => {
            if (root.TryGetProperty("choices", out var choices) &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("delta", out var delta)) {
                if (ReadString(delta, "reasoning_content") is { Length: > 0 } reasoningDelta) {
                    await onEvent(AgentEvent.ReasoningDelta(reasoningDelta));
                }

                if (ReadString(delta, "content") is { Length: > 0 } textDelta) {
                    text.Append(textDelta);
                    await onEvent(AgentEvent.TextDelta(textDelta));
                }

                if (delta.TryGetProperty("tool_calls", out var toolCallsElement) &&
                    toolCallsElement.ValueKind == JsonValueKind.Array) {
                    AccumulateToolCalls(toolCallsElement, toolCallAccumulators);
                }
            }

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object) {
                (promptTokens, completionTokens, cachedTokens) = ReadUsage(usage);
            }

            return false;
        }, ct);

        if (!done && !interrupted) {
            throw new InvalidOperationException("Completion stream ended before [DONE]");
        }

        var calls = toolCallAccumulators.Values.Select(a => a.ToToolCall()).ToList();
        return new RoundResult(text.ToString(), calls, promptTokens, completionTokens, cachedTokens, interrupted);
    }

    private static void AccumulateToolCalls(JsonElement toolCallsElement,
        SortedDictionary<int, ToolCallAccumulator> accumulators) {
        foreach (var callElement in toolCallsElement.EnumerateArray()) {
            var index = callElement.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var parsed)
                ? parsed
                : accumulators.Count;
            if (!accumulators.TryGetValue(index, out var acc)) {
                acc = new ToolCallAccumulator();
                accumulators[index] = acc;
            }

            if (ReadString(callElement, "id") is { Length: > 0 } id) {
                acc.Id = id;
            }

            if (!callElement.TryGetProperty("function", out var fn)) continue;

            if (ReadString(fn, "name") is { Length: > 0 } name) {
                acc.Name = name;
            }

            if (ReadString(fn, "arguments") is { Length: > 0 } args) {
                acc.Arguments.Append(args);
            }
        }
    }

    // Advertised on every request (including compaction) to preserve prefix cache.
    private List<JsonElement> BuildTools() => [
        .. Model.Tools.Select(WrapModelTool),
        .. LocalTools.Select(WrapFunctionTool)
    ];

    private static JsonElement WrapFunctionTool(ToolDefinition def) => JsonSerializer.SerializeToElement(
        new CompletionsToolDto {
            Function = new FunctionSchemaDto {
                Name = def.Name,
                Description = def.Description,
                Parameters = def.Parameters
            }
        },
        AgentJsonContext.Default.CompletionsToolDto);

    private static JsonElement WrapModelTool(JsonElement tool) {
        if (tool.TryGetProperty("function", out _) ||
            !tool.TryGetProperty("name", out var name) ||
            !tool.TryGetProperty("parameters", out var parameters)) {
            return tool;
        }

        var desc = ReadString(tool, "description") ?? string.Empty;
        return WrapFunctionTool(new ToolDefinition(name.GetString() ?? string.Empty, desc, parameters));
    }

    private List<ChatMessage> BuildMessages(IReadOnlyList<ConversationMessage> conversation) {
        var messages = new List<ChatMessage> { new() { Role = "system", Content = Instructions } };
        List<ToolCallDto>? pendingToolCalls = null;

        foreach (var message in conversation) {
            switch (message) {
                case FunctionCallMessage call: {
                    var dto = new ToolCallDto {
                        Id = call.CallId,
                        Function = new FunctionDto { Name = call.Name, Arguments = call.Arguments }
                    };
                    if (pendingToolCalls is not null) {
                        pendingToolCalls.Add(dto);
                    } else {
                        pendingToolCalls = [dto];
                        messages.Add(new ChatMessage { Role = "assistant", ToolCalls = pendingToolCalls });
                    }

                    break;
                }
                case FunctionCallOutputMessage output:
                    pendingToolCalls = null;
                    messages.Add(new ChatMessage { Role = "tool", ToolCallId = output.CallId, Content = output.Output });
                    break;
                case UserMessage user:
                    pendingToolCalls = null;
                    messages.Add(new ChatMessage { Role = "user", Content = user.Content });
                    break;
                case AssistantMessage assistant:
                    pendingToolCalls = null;
                    messages.Add(new ChatMessage { Role = "assistant", Content = assistant.Content });
                    break;
            }
        }

        return messages;
    }

    private sealed class ToolCallAccumulator {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public StringBuilder Arguments { get; } = new();

        public ToolCall ToToolCall() => new(Id, Name, Arguments.ToString());
    }

    internal sealed class CompletionsRequest {
        public required string Model { get; init; }
        public required List<ChatMessage> Messages { get; init; }
        public bool Stream { get; init; }

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; init; }

        [JsonPropertyName("reasoning_effort")]
        public string? ReasoningEffort { get; init; }

        public required List<JsonElement> Tools { get; init; }
    }

    internal sealed class CompletionsToolDto {
        public string Type { get; init; } = "function";
        public required FunctionSchemaDto Function { get; init; }
    }

    internal sealed class FunctionSchemaDto {
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required JsonElement Parameters { get; init; }
    }

    internal sealed class ChatMessage {
        public required string Role { get; init; }
        public string? Content { get; init; }

        [JsonPropertyName("tool_call_id")]
        public string? ToolCallId { get; init; }

        [JsonPropertyName("tool_calls")]
        public List<ToolCallDto>? ToolCalls { get; set; }
    }

    internal sealed class ToolCallDto {
        public required string Id { get; init; }
        public string Type { get; init; } = "function";
        public required FunctionDto Function { get; init; }
    }

    internal sealed class FunctionDto {
        public required string Name { get; init; }
        public required string Arguments { get; init; }
    }
}
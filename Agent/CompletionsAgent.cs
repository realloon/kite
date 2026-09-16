using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kite.Configuration;
using Kite.Tools;

namespace Kite.Agent;

/// <summary>
/// Agent backed by an OpenAI Chat Completions API-compatible endpoint.
/// </summary>
internal sealed class CompletionsAgent(
    string apiKey,
    string baseUrl,
    string model,
    string? instructions,
    string? reasoningEffort,
    int? maxTokens,
    IReadOnlyList<JsonElement> modelTools,
    string providerId)
    : Agent(apiKey, baseUrl, "/chat/completions", providerId) {
    private readonly string _instructions = instructions ?? string.Empty;

    private static readonly JsonElement[] LocalTools = [
        .. new[] { RunShell.Definition, SkillTool.Definition }
            .Concat(FileTools.Definitions)
            .Select(WrapFunctionTool)
    ];

    public override string DisplayName => reasoningEffort is null ? model : $"{model} · {reasoningEffort}";

    public static CompletionsAgent Create(string apiKey, ModelPreset model, string? variant, string? instructions) {
        if (model.Variants.Count > 0) {
            if (variant is null || !model.Variants.Contains(variant, StringComparer.Ordinal)) {
                throw new InvalidOperationException(
                    $"Model '{model.Id}' does not support reasoning effort '{variant}'. Available: {string.Join(" / ", model.Variants)}");
            }
        } else {
            variant = null;
        }

        return new CompletionsAgent(
            apiKey,
            model.BaseUrl ?? throw new InvalidOperationException($"Model '{model.Id}' has no baseUrl"),
            model.Id,
            instructions,
            variant,
            model.Limit.Output,
            model.Tools,
            model.ProviderId);
    }

    public override async Task<AgentReply> StreamReplyAsync(
        IReadOnlyList<ConversationMessage> conversation,
        string sessionId,
        Func<AgentEvent, Task> onEvent,
        Func<IReadOnlyList<ToolCall>, CancellationToken, Task<IReadOnlyList<string>>>? executeToolCalls,
        CancellationToken cancellationToken) {
        var messages = BuildMessages(conversation);
        var promptTokens = 0;
        var completionTokens = 0;
        var cachedTokens = 0;
        var contextTokens = 0;

        try {
            while (!cancellationToken.IsCancellationRequested) {
                var round = await StreamRoundAsync(messages, sessionId, onEvent, executeToolCalls, cancellationToken);
                promptTokens += round.PromptTokens;
                completionTokens += round.CompletionTokens;
                cachedTokens += round.CachedTokens;
                contextTokens = round.PromptTokens;

                if (round.Calls.Count == 0 || executeToolCalls is null) {
                    break;
                }

                var assistantMessage = new ChatMessage {
                    Role = "assistant",
                    Content = round.Text.Length > 0 ? round.Text : null,
                    ToolCalls = round.Calls.Select(call => new ToolCallDto {
                        Id = call.Id,
                        Type = "function",
                        Function = new FunctionDto {
                            Name = call.Name,
                            Arguments = call.Arguments
                        }
                    }).ToList()
                };
                messages.Add(assistantMessage);

                var outputs = await executeToolCalls(round.Calls, cancellationToken);
                for (var i = 0; i < round.Calls.Count; i += 1) {
                    messages.Add(new ChatMessage {
                        Role = "tool",
                        ToolCallId = round.Calls[i].Id,
                        Content = outputs[i]
                    });
                }
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }

        return new AgentReply(promptTokens, completionTokens, cachedTokens, contextTokens);
    }

    private async Task<RoundResult> StreamRoundAsync(
        List<ChatMessage> messages,
        string sessionId,
        Func<AgentEvent, Task> onEvent,
        Func<IReadOnlyList<ToolCall>, CancellationToken, Task<IReadOnlyList<string>>>? executeToolCalls,
        CancellationToken cancellationToken) {
        var request = new CompletionsRequest {
            Model = model,
            Messages = messages,
            Stream = true,
            MaxTokens = maxTokens,
            ReasoningEffort = reasoningEffort,
            Tools = BuildTools(executeToolCalls is not null)
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(request, KiteJsonContext.Default.CompletionsRequest);
        using var httpRequest = CreateRequest(json, sessionId);
        using var response = await SendAsync(httpRequest, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = new StringBuilder();
        var toolCallAccumulators = new SortedDictionary<int, ToolCallAccumulator>();
        var promptTokens = 0;
        var completionTokens = 0;
        var cachedTokens = 0;
        var done = false;

        try {
            while (await reader.ReadLineAsync(cancellationToken) is { } line) {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) {
                    continue;
                }

                var payload = line.AsSpan(5).Trim();
                if (payload.IsEmpty) {
                    continue;
                }

                if (payload.SequenceEqual("[DONE]".AsSpan())) {
                    done = true;
                    break;
                }

                using var document = JsonDocument.Parse(payload.ToString());
                var root = document.RootElement;
                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0) {
                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var delta)) {
                        if (delta.TryGetProperty("reasoning_content", out var reasoning) &&
                            reasoning.GetString() is { Length: > 0 } reasoningDelta) {
                            await onEvent(AgentEvent.ReasoningDelta(reasoningDelta));
                        }

                        if (delta.TryGetProperty("content", out var content) &&
                            content.GetString() is { Length: > 0 } textDelta) {
                            text.Append(textDelta);
                            await onEvent(AgentEvent.TextDelta(textDelta));
                        }

                        if (delta.TryGetProperty("tool_calls", out var toolCallsElement) &&
                            toolCallsElement.ValueKind == JsonValueKind.Array) {
                            foreach (var callElement in toolCallsElement.EnumerateArray()) {
                                var index = callElement.TryGetProperty("index", out var idx)
                                    ? idx.GetInt32()
                                    : toolCallAccumulators.Count;
                                if (!toolCallAccumulators.TryGetValue(index, out var acc)) {
                                    acc = new ToolCallAccumulator();
                                    toolCallAccumulators[index] = acc;
                                }

                                if (callElement.TryGetProperty("id", out var idProp) &&
                                    idProp.GetString() is { Length: > 0 } id) {
                                    acc.Id = id;
                                }

                                if (callElement.TryGetProperty("function", out var fn)) {
                                    if (fn.TryGetProperty("name", out var nameProp) && nameProp.GetString() is
                                            { Length: > 0 } name) {
                                        acc.Name = name;
                                    }

                                    if (fn.TryGetProperty("arguments", out var argsProp) && argsProp.GetString() is
                                            { Length: > 0 } args) {
                                        acc.Arguments.Append(args);
                                    }
                                }
                            }
                        }
                    }
                }

                if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) {
                    continue;
                }

                promptTokens = ReadInt(usage, "prompt_tokens");
                completionTokens = ReadInt(usage, "completion_tokens");
                var details = usage.TryGetProperty("prompt_tokens_details", out var promptDetails)
                    ? promptDetails
                    : usage.TryGetProperty("input_tokens_details", out var inputDetails)
                        ? inputDetails
                        : default;
                if (details.ValueKind == JsonValueKind.Object &&
                    details.TryGetProperty("cached_tokens", out var cached)) {
                    cachedTokens = cached.GetInt32();
                }
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            var partialCalls = toolCallAccumulators.Values.Select(a => a.ToToolCall()).ToList();
            return new RoundResult(text.ToString(), partialCalls, promptTokens, completionTokens, cachedTokens);
        } catch (JsonException ex) {
            throw new InvalidOperationException("Completion stream contains invalid JSON", ex);
        }

        if (!done && !cancellationToken.IsCancellationRequested) {
            throw new InvalidOperationException("Completion stream ended before [DONE]");
        }

        var calls = toolCallAccumulators.Values.Select(a => a.ToToolCall()).ToList();
        return new RoundResult(text.ToString(), calls, promptTokens, completionTokens, cachedTokens);
    }

    private List<JsonElement>? BuildTools(bool includeLocalTools) {
        if (modelTools.Count == 0 && !includeLocalTools) {
            return null;
        }

        var tools = new List<JsonElement>(modelTools.Count + (includeLocalTools ? LocalTools.Length : 0));
        foreach (var tool in modelTools) {
            tools.Add(WrapModelTool(tool));
        }

        if (includeLocalTools) {
            tools.AddRange(LocalTools);
        }

        return tools;
    }

    private static JsonElement WrapFunctionTool(ToolDefinition def) {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) {
            writer.WriteStartObject();
            writer.WriteString("type", "function");
            writer.WriteStartObject("function");
            writer.WriteString("name", def.Name);
            writer.WriteString("description", def.Description);
            writer.WritePropertyName("parameters");
            def.Parameters.WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement WrapModelTool(JsonElement tool) {
        if (tool.TryGetProperty("function", out _)) {
            return tool;
        }

        if (tool.TryGetProperty("name", out var name) && tool.TryGetProperty("parameters", out var parameters)) {
            var desc = tool.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;
            return WrapFunctionTool(new ToolDefinition(name.GetString() ?? string.Empty, desc, parameters));
        }

        return tool;
    }

    private List<ChatMessage> BuildMessages(IReadOnlyList<ConversationMessage> conversation) {
        var messages = new List<ChatMessage>();
        if (_instructions.Length > 0) {
            messages.Add(new ChatMessage { Role = "system", Content = _instructions });
        }

        ChatMessage? pendingAssistantToolCalls = null;

        foreach (var message in conversation) {
            if (message.Type == ConversationMessage.FunctionCallType) {
                var toolCall = new ToolCallDto {
                    Id = message.CallId ?? string.Empty,
                    Function = new FunctionDto {
                        Name = message.Name ?? string.Empty,
                        Arguments = message.Arguments ?? string.Empty
                    }
                };

                if (pendingAssistantToolCalls is not null) {
                    pendingAssistantToolCalls.ToolCalls ??= [];
                    pendingAssistantToolCalls.ToolCalls.Add(toolCall);
                } else {
                    pendingAssistantToolCalls = new ChatMessage {
                        Role = "assistant",
                        ToolCalls = [toolCall]
                    };
                    messages.Add(pendingAssistantToolCalls);
                }

                continue;
            }

            pendingAssistantToolCalls = null;

            if (message.Type == ConversationMessage.FunctionCallOutputType) {
                messages.Add(new ChatMessage {
                    Role = "tool",
                    ToolCallId = message.CallId,
                    Content = message.Content
                });
                continue;
            }

            messages.Add(new ChatMessage { Role = message.Role, Content = message.Content });
        }

        return messages;
    }

    private static int ReadInt(JsonElement value, string property) =>
        value.TryGetProperty(property, out var element) && element.TryGetInt32(out var result) ? result : 0;

    private sealed class ToolCallAccumulator {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public StringBuilder Arguments { get; } = new();

        public ToolCall ToToolCall() => new(Id, Name, Arguments.ToString());
    }

    private sealed record RoundResult(
        string Text,
        List<ToolCall> Calls,
        int PromptTokens,
        int CompletionTokens,
        int CachedTokens);

    internal sealed class CompletionsRequest {
        public string Model { get; init; } = string.Empty;
        public List<ChatMessage> Messages { get; init; } = [];
        public bool Stream { get; init; }

        [JsonPropertyName("max_tokens")]
        public int? MaxTokens { get; init; }

        [JsonPropertyName("reasoning_effort")]
        public string? ReasoningEffort { get; init; }

        public List<JsonElement>? Tools { get; init; }
    }

    internal sealed class ChatMessage {
        public string Role { get; init; } = string.Empty;
        public string? Content { get; init; }

        [JsonPropertyName("tool_call_id")]
        public string? ToolCallId { get; init; }

        [JsonPropertyName("tool_calls")]
        public List<ToolCallDto>? ToolCalls { get; set; }
    }

    internal sealed class ToolCallDto {
        public string Id { get; init; } = string.Empty;
        public string Type { get; init; } = "function";
        public FunctionDto Function { get; init; } = new();
    }

    internal sealed class FunctionDto {
        public string Name { get; init; } = string.Empty;
        public string Arguments { get; init; } = string.Empty;
    }
}
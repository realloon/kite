using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kite.Configuration;
using Kite.Tools;

namespace Kite.Agent;

/// <summary>Agent backed by an OpenAI Responses API-compatible endpoint.</summary>
internal sealed class ResponsesAgent(
    string apiKey,
    string baseUrl,
    string model,
    string? instructions,
    string? reasoningEffort,
    int? maxOutputTokens,
    IReadOnlyList<JsonElement> modelTools,
    string providerId = "") : Agent(apiKey, baseUrl, "/responses", providerId) {
    private readonly string _instructions = instructions ?? string.Empty;

    private static readonly JsonElement[] LocalTools = [
        .. new[] { RunShell.Definition, SkillTool.Definition }
            .Concat(FileTools.Definitions)
            .Select(tool => JsonSerializer.SerializeToElement(tool, KiteJsonContext.Default.ToolDefinition))
    ];

    public override string DisplayName => reasoningEffort is null ? model : $"{model} · {reasoningEffort}";

    public static ResponsesAgent Create(string apiKey, ModelPreset model, string? variant, string? instructions) {
        if (model.Variants.Count > 0) {
            if (variant is null || !model.Variants.Contains(variant, StringComparer.Ordinal)) {
                throw new InvalidOperationException(
                    $"Model '{model.Id}' does not support reasoning effort '{variant}'. Available: {string.Join(" / ", model.Variants)}");
            }
        } else {
            variant = null;
        }

        return new ResponsesAgent(
            apiKey,
            model.BaseUrl ?? throw new InvalidOperationException($"Model '{model.Id}' has no baseUrl"),
            model.Id,
            instructions,
            variant,
            model.Limit.Output,
            model.Tools,
            model.ProviderId
        );
    }

    public override async Task<AgentReply> StreamReplyAsync(
        IReadOnlyList<ConversationMessage> conversation,
        string sessionId,
        Func<AgentEvent, Task> onEvent,
        Func<IReadOnlyList<ToolCall>, CancellationToken, Task<IReadOnlyList<string>>>? executeToolCalls,
        CancellationToken cancellationToken) {
        var items = conversation.Select(ToInputItem).ToList();

        var promptTokens = 0;
        var completionTokens = 0;
        var cachedTokens = 0;
        var contextTokens = 0;
        try {
            while (!cancellationToken.IsCancellationRequested) {
                var round = await StreamRoundAsync(items, sessionId, onEvent, executeToolCalls, cancellationToken);
                promptTokens += round.PromptTokens;
                completionTokens += round.CompletionTokens;
                cachedTokens += round.CachedTokens;
                contextTokens = round.PromptTokens;

                if (round.Interrupted || round.Calls.Count == 0 || executeToolCalls is null) {
                    break;
                }

                if (round.Text.Length > 0) {
                    items.Add(new InputItem { Role = "assistant", Content = round.Text });
                }

                items.AddRange(round.Calls.Select(call => new InputItem {
                    Type = "function_call",
                    CallId = call.Id, Name = call.Name,
                    Arguments = call.Arguments
                }));

                var outputs = await executeToolCalls(round.Calls, cancellationToken);
                items.AddRange(round.Calls.Select((t, index) => new InputItem {
                    Type = "function_call_output",
                    CallId = t.Id,
                    Output = outputs[index]
                }));
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }

        return new AgentReply(promptTokens, completionTokens, cachedTokens, contextTokens);
    }

    private async Task<RoundResult> StreamRoundAsync(
        List<InputItem> items,
        string sessionId,
        Func<AgentEvent, Task> onEvent,
        Func<IReadOnlyList<ToolCall>, CancellationToken, Task<IReadOnlyList<string>>>? executeToolCalls,
        CancellationToken cancellationToken) {
        var request = new ResponsesRequest {
            Model = model,
            Input = items,
            Instructions = _instructions.Length > 0 ? _instructions : null,
            Stream = true,
            Reasoning = reasoningEffort is null ? null : new ReasoningRequest { Effort = reasoningEffort },
            MaxOutputTokens = maxOutputTokens,
            Tools = BuildTools(executeToolCalls is not null)
        };

        // Pre-serialize the body: explicit Content-Length instead of chunked
        // upload, which some servers/gateways fail to parse. AOT-safe via source gen.
        var json = JsonSerializer.SerializeToUtf8Bytes(request, KiteJsonContext.Default.ResponsesRequest);
        using var httpRequest = CreateRequest(json, sessionId);
        using var response = await SendAsync(httpRequest, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var text = new StringBuilder();
        var promptTokens = 0;
        var completionTokens = 0;
        var cachedTokens = 0;
        var calls = new List<ToolCall>();
        var terminalEventSeen = false;
        var interrupted = false;
        string? failure = null;

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
                    terminalEventSeen = true; // Defensive: some gateways send [DONE]
                    break;
                }

                using var doc = JsonDocument.Parse(payload.ToString());
                var type = doc.RootElement.GetProperty("type").GetString();
                switch (type) {
                    case "response.output_text.delta": {
                        var delta = doc.RootElement.GetProperty("delta").GetString() ?? string.Empty;
                        if (delta.Length > 0) {
                            text.Append(delta);
                            await onEvent(AgentEvent.TextDelta(delta));
                        }

                        break;
                    }
                    case "response.reasoning_summary_text.delta":
                    case "response.reasoning_text.delta": {
                        var delta = doc.RootElement.GetProperty("delta").GetString() ?? string.Empty;
                        if (delta.Length > 0) {
                            await onEvent(AgentEvent.ReasoningDelta(delta));
                        }

                        break;
                    }
                    case "response.completed":
                    case "response.incomplete": {
                        (promptTokens, completionTokens, cachedTokens) = ReadUsage(doc.RootElement);
                        calls = ReadFunctionCalls(doc.RootElement);
                        terminalEventSeen = true;
                        break;
                    }
                    case "response.failed": {
                        failure = TryReadFailureMessage(doc.RootElement) ?? "Unknown error";
                        terminalEventSeen = true;
                        break;
                    }
                }

                if (terminalEventSeen || failure is not null) {
                    break; // A terminal event ends the stream; do not wait for the server to close
                }
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            interrupted = true;
        } catch (JsonException ex) {
            throw new InvalidOperationException("Response stream contains invalid JSON", ex);
        }

        if (failure is not null) {
            throw new InvalidOperationException(failure);
        }

        if (!terminalEventSeen && !cancellationToken.IsCancellationRequested) {
            throw new InvalidOperationException("Response stream ended before response.completed");
        }

        return new RoundResult(text.ToString(), calls, promptTokens, completionTokens, cachedTokens, interrupted);
    }

    private List<JsonElement>? BuildTools(bool includeLocalTools) {
        if (modelTools.Count == 0 && !includeLocalTools) {
            return null;
        }

        var tools = new List<JsonElement>(modelTools);

        if (includeLocalTools) {
            tools.AddRange(LocalTools);
        }

        return tools;
    }

    private static InputItem ToInputItem(ConversationMessage message) => message.Type switch {
        null => new InputItem { Role = message.Role, Content = message.Content },
        ConversationMessage.FunctionCallType => new InputItem {
            Type = ConversationMessage.FunctionCallType,
            CallId = message.CallId,
            Name = message.Name,
            Arguments = message.Arguments
        },
        ConversationMessage.FunctionCallOutputType => new InputItem {
            Type = ConversationMessage.FunctionCallOutputType,
            CallId = message.CallId,
            Output = message.Content
        },
        _ => throw new InvalidOperationException($"Unknown conversation item type: {message.Type}")
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

    private static string? ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) ? property.GetString() : null;

    private static (int Prompt, int Completion, int Cached) ReadUsage(JsonElement root) {
        var container = root.TryGetProperty("response", out var response) ? response : root;
        if (!container.TryGetProperty("usage", out var usage)) {
            return (0, 0, 0);
        }

        var promptTokens = ReadInt(usage, "input_tokens", "prompt_tokens");
        var completionTokens = ReadInt(usage, "output_tokens", "completion_tokens");
        var details = usage.TryGetProperty("input_token_details", out var inputDetails)
            ? inputDetails
            : usage.TryGetProperty("prompt_tokens_details", out var promptDetails)
                ? promptDetails
                : usage.TryGetProperty("input_tokens_details", out var inputDetails2)
                    ? inputDetails2
                    : default;
        var cachedTokens = details.ValueKind == JsonValueKind.Object &&
                           details.TryGetProperty("cached_tokens", out var cached)
            ? cached.GetInt32()
            : 0;
        return (promptTokens, completionTokens, cachedTokens);
    }

    private static int ReadInt(JsonElement value, string name, string fallback) =>
        value.TryGetProperty(name, out var primary) ? primary.GetInt32()
        : value.TryGetProperty(fallback, out var secondary) ? secondary.GetInt32()
        : 0;

    private static string? TryReadFailureMessage(JsonElement root) {
        if (root.TryGetProperty("response", out var response)
            && response.TryGetProperty("error", out var error)
            && error.TryGetProperty("message", out var message)) {
            return message.GetString();
        }

        return null;
    }

    private sealed record RoundResult(
        string Text,
        List<ToolCall> Calls,
        int PromptTokens,
        int CompletionTokens,
        int CachedTokens,
        bool Interrupted);

    internal sealed class ResponsesRequest {
        public string Model { get; init; } = string.Empty;
        public List<InputItem>? Input { get; init; }
        public string? Instructions { get; init; }
        public bool Stream { get; init; }
        public ReasoningRequest? Reasoning { get; init; }

        [JsonPropertyName("max_output_tokens")]
        public int? MaxOutputTokens { get; init; }

        public List<JsonElement>? Tools { get; init; }
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
        public string? Effort { get; init; }
    }
}
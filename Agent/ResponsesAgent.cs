using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kite.Configuration;
using Kite.Context;
using Kite.Tools;

namespace Kite.Agent;

/// <summary>
/// Real agent backed by an OpenAI Responses API-compatible endpoint.
/// Zero third-party dependencies: HttpClient + System.Text.Json source
/// generation (AOT-safe). Stateless protocol: the full conversation history
/// is sent with every request.
/// Tool loop: request → stream text → parse function_call items from the
/// terminal event → execute tools → feed call/output items back → request
/// again, until the model answers with plain text.
/// </summary>
public sealed class ResponsesAgent(
    string apiKey,
    string baseUrl,
    string model,
    string? instructions,
    string? reasoningEffort,
    int? maxOutputTokens,
    IReadOnlyList<JsonElement>? modelTools) : IDisposable {
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly Uri _endpoint = new(new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"), "responses");
    private readonly string _instructions = instructions ?? string.Empty;
    private readonly IReadOnlyList<JsonElement> _modelTools = modelTools ?? [];

    private static readonly JsonElement[] LocalTools = [
        .. new[] { RunShell.Definition }
            .Concat(FileTools.Definitions)
            .Select(tool => JsonSerializer.SerializeToElement(tool, KiteJsonContext.Default.ToolDefinition))
    ];

    private string ModelName { get; } = model;

    public string DisplayName => reasoningEffort is null ? ModelName : $"{ModelName} · {reasoningEffort}";

    public static ResponsesAgent? FromState(ModelCatalog catalog, KiteAuth auth, KiteState state, string workspace) {
        state.Validate();
        if (state.Provider is null) {
            return null;
        }

        var provider = catalog.FindProvider(state.Provider)
                       ?? throw new InvalidOperationException($"Unknown provider '{state.Provider}' in state.json");
        if (state.Model is null || state.Variant is null) {
            return null;
        }

        var model = catalog.FindModel(provider.Id, state.Model)
                    ?? throw new InvalidOperationException(
                        $"Unknown model '{state.Model}' for provider '{provider.Id}' in state.json");
        var apiKey = auth.Get(provider.Id);
        if (apiKey is null) {
            return null;
        }

        var instructions = Instruction.Build(model.Instructions, workspace);
        return Create(apiKey, model, state.Variant, instructions);
    }

    public static ResponsesAgent Create(string apiKey, ModelPreset model, string variant, string? instructions) {
        var variants = model.Variants ?? throw new InvalidOperationException($"Model '{model.Id}' has no variants");
        if (!variants.Contains(variant, StringComparer.OrdinalIgnoreCase)) {
            throw new InvalidOperationException(
                $"Model '{model.Id}' does not support reasoning effort '{variant}'. Available: {string.Join(" / ", variants)}");
        }

        return new ResponsesAgent(
            apiKey,
            model.BaseUrl ?? throw new InvalidOperationException($"Model '{model.Id}' has no baseUrl"),
            model.Id,
            instructions,
            variant,
            model.Limit?.Output,
            model.Tools
        );
    }

    public async Task<AgentReply> StreamReplyAsync(
        IReadOnlyList<ConversationMessage> conversation,
        Func<AgentEvent, Task> onEvent,
        Func<IReadOnlyList<ToolCall>, CancellationToken, Task<IReadOnlyList<string>>>? executeToolCalls,
        CancellationToken cancellationToken) {
        var items = conversation.Select(ToInputItem).ToList();

        var promptTokens = 0;
        var completionTokens = 0;
        while (!cancellationToken.IsCancellationRequested) {
            RoundResult round;
            try {
                round = await StreamRoundAsync(items, onEvent, executeToolCalls, cancellationToken);
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                break;
            }

            promptTokens += round.PromptTokens;
            completionTokens += round.CompletionTokens;

            if (round.Interrupted || round.Calls.Count == 0 || executeToolCalls is null) {
                break;
            }

            if (round.Text.Length > 0) {
                items.Add(new InputItem { Role = "assistant", Content = round.Text });
            }

            try {
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
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                break;
            }
        }

        return new AgentReply(promptTokens, completionTokens);
    }

    public void Dispose() => _http.Dispose();

    private async Task<RoundResult> StreamRoundAsync(
        List<InputItem> items,
        Func<AgentEvent, Task> onEvent,
        Func<IReadOnlyList<ToolCall>, CancellationToken, Task<IReadOnlyList<string>>>? executeToolCalls,
        CancellationToken cancellationToken) {
        var request = new ResponsesRequest {
            Model = ModelName,
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
        using var content = new ReadOnlyMemoryContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        httpRequest.Content = content;
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");

        using var response =
            await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode) {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var message = TryReadErrorMessage(body) ?? $"HTTP {(int)response.StatusCode}";
            throw new InvalidOperationException($"{message}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var text = new StringBuilder();
        var promptTokens = 0;
        var completionTokens = 0;
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
                        (promptTokens, completionTokens) = ReadUsage(doc.RootElement);
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

        return new RoundResult(text.ToString(), calls, promptTokens, completionTokens, interrupted);
    }

    private List<JsonElement>? BuildTools(bool includeLocalTools) {
        if (_modelTools.Count == 0 && !includeLocalTools) return null;

        var tools = new List<JsonElement>(_modelTools);

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
        var calls = new List<ToolCall>();
        if (!root.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("output", out var output)) {
            return calls;
        }

        foreach (var item in output.EnumerateArray()) {
            if (!item.TryGetProperty("type", out var t) || t.GetString() != "function_call") continue;

            var id = (item.TryGetProperty("call_id", out var cid) ? cid.GetString() : null)
                     ?? (item.TryGetProperty("id", out var iid) ? iid.GetString() : null)
                     ?? string.Empty;
            var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            var arguments = item.TryGetProperty("arguments", out var a)
                ? a.GetString() ?? string.Empty
                : string.Empty;
            calls.Add(new ToolCall(id, name, arguments));
        }

        return calls;
    }

    private static (int Prompt, int Completion) ReadUsage(JsonElement root) {
        var usage = root.GetProperty("response").GetProperty("usage");
        return (usage.GetProperty("input_tokens").GetInt32(), usage.GetProperty("output_tokens").GetInt32());
    }

    private static string? TryReadFailureMessage(JsonElement root) {
        if (root.TryGetProperty("response", out var response)
            && response.TryGetProperty("error", out var error)
            && error.TryGetProperty("message", out var message)) {
            return message.GetString();
        }

        return null;
    }

    private static string? TryReadErrorMessage(string body) {
        if (body.Length == 0) {
            return null;
        }

        try {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message)) {
                return message.GetString();
            }
        } catch (JsonException) {
            // Non-JSON error body (e.g. gateway HTML)
        }

        return null;
    }

    private sealed record RoundResult(
        string Text,
        List<ToolCall> Calls,
        int PromptTokens,
        int CompletionTokens,
        bool Interrupted);

    internal sealed class ResponsesRequest {
        public string Model { get; set; } = string.Empty;
        public List<InputItem>? Input { get; set; }
        public string? Instructions { get; set; }
        public bool Stream { get; set; }
        public ReasoningRequest? Reasoning { get; set; }

        [JsonPropertyName("max_output_tokens")]
        public int? MaxOutputTokens { get; set; }

        public List<JsonElement>? Tools { get; set; }
    }

    internal sealed class InputItem {
        public string Type { get; set; } = "message";
        public string? Role { get; set; }
        public string? Content { get; set; }

        [JsonPropertyName("call_id")]
        public string? CallId { get; set; }

        public string? Name { get; set; }
        public string? Arguments { get; set; }
        public string? Output { get; set; }
    }

    internal sealed class ReasoningRequest {
        public string? Effort { get; set; }
    }
}
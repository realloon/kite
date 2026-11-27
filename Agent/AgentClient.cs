using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kite.App;
using Kite.Config;

namespace Kite.Agent;

internal abstract class AgentClient(
    string apiKey,
    ModelPreset model,
    string route,
    string? variant,
    string instructions,
    IReadOnlyList<ToolDefinition> localTools) : IDisposable {
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly Uri _endpoint = ResolveEndpoint(model.BaseUrl, route);

    public ModelPreset Model { get; } = model;
    protected string? ReasoningEffort { get; } = ResolveVariant(model, variant);
    protected string Instructions { get; } = instructions;
    protected IReadOnlyList<ToolDefinition> LocalTools { get; } = localTools;

    public string DisplayName => ReasoningEffort is null ? Model.Id : $"{Model.Id} · {ReasoningEffort}";

    public void Dispose() => _http.Dispose();

    public abstract Task<AgentReply> StreamReplyAsync(
        IReadOnlyList<ConversationMessage> conversation,
        string sessionId,
        Func<AgentEvent, Task> onEvent,
        Func<IReadOnlyList<ToolCall>, CancellationToken, Task<IReadOnlyList<string>>>? executeToolCalls,
        CancellationToken ct);

    protected async Task<AgentReply> RunToolLoopAsync<TItem>(
        List<TItem> items,
        Func<List<TItem>, Task<RoundResult>> streamRound,
        Action<List<TItem>, RoundResult, IReadOnlyList<string>> appendToolRound,
        Func<IReadOnlyList<ToolCall>, CancellationToken, Task<IReadOnlyList<string>>>? executeToolCalls,
        CancellationToken ct) {
        var promptTokens = 0;
        var completionTokens = 0;
        var cachedTokens = 0;
        var contextTokens = 0;

        try {
            while (!ct.IsCancellationRequested) {
                var round = await streamRound(items);
                promptTokens += round.PromptTokens;
                completionTokens += round.CompletionTokens;
                cachedTokens += round.CachedTokens;
                contextTokens = round.PromptTokens;

                if (round.Interrupted || round.Calls.Count == 0 || executeToolCalls is null) break;

                var outputs = await executeToolCalls(round.Calls, ct);
                appendToolRound(items, round, outputs);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }

        return new AgentReply(promptTokens, completionTokens, cachedTokens, contextTokens);
    }

    // Pre-serialized for explicit Content-Length; some gateways reject chunked uploads.
    protected HttpRequestMessage CreateRequest(ReadOnlyMemory<byte> body, string sessionId) {
        var content = new ReadOnlyMemoryContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.UserAgent.ParseAdd($"kite/{KiteVersion.Value}");

        if (Model.ProviderId == "opencode-go") {
            request.Headers.TryAddWithoutValidation("x-opencode-session", sessionId);
        }

        return request;
    }

    protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode) {
            return response;
        }

        using (response) {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(ReadError(body) ?? $"HTTP {(int)response.StatusCode}");
        }
    }

    protected static async Task<(bool Completed, bool Interrupted)> ReadSseAsync(
        HttpResponseMessage response,
        string streamName,
        Func<JsonElement, Task<bool>> onPayload,
        CancellationToken ct) {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        try {
            while (await reader.ReadLineAsync(ct) is { } line) {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var payload = line.AsSpan(5).Trim();
                if (payload.IsEmpty) continue;

                if (payload.SequenceEqual("[DONE]".AsSpan())) {
                    return (true, false);
                }

                using var document = JsonDocument.Parse(payload.ToString());
                if (await onPayload(document.RootElement)) {
                    return (true, false);
                }
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return (false, true);
        } catch (JsonException ex) {
            throw new InvalidOperationException($"{streamName} stream contains invalid JSON", ex);
        }

        return (false, false);
    }

    protected static (int Prompt, int Completion, int Cached) ReadUsage(JsonElement usage) {
        var promptTokens = ReadInt(usage, "input_tokens", "prompt_tokens");
        var completionTokens = ReadInt(usage, "output_tokens", "completion_tokens");
        var details = usage.TryGetProperty("input_token_details", out var inputDetails)
            ? inputDetails
            : usage.TryGetProperty("prompt_tokens_details", out var promptDetails)
                ? promptDetails
                : usage.TryGetProperty("input_tokens_details", out var inputDetails2)
                    ? inputDetails2
                    : default;
        var cachedTokens = details.ValueKind == JsonValueKind.Object ? ReadInt(details, "cached_tokens") : 0;
        return (promptTokens, completionTokens, cachedTokens);
    }

    protected static int ReadInt(JsonElement value, string name, string? fallback = null) {
        if (value.TryGetProperty(name, out var primary) && primary.TryGetInt32(out var primaryResult)) {
            return primaryResult;
        }

        if (fallback is not null &&
            value.TryGetProperty(fallback, out var secondary) &&
            secondary.TryGetInt32(out var secondaryResult)) {
            return secondaryResult;
        }

        return 0;
    }

    protected static string? ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) ? property.GetString() : null;

    protected sealed record RoundResult(
        string Text,
        List<ToolCall> Calls,
        int PromptTokens,
        int CompletionTokens,
        int CachedTokens,
        bool Interrupted);

    private static string? ResolveVariant(ModelPreset model, string? variant) {
        if (model.Variants.Count == 0) {
            return null;
        }

        if (variant is null || !model.Variants.Contains(variant, StringComparer.Ordinal)) {
            throw new InvalidOperationException(
                $"Model '{model.Id}' does not support reasoning effort '{variant}'. Available: {string.Join(" / ", model.Variants)}");
        }

        return variant;
    }

    private static Uri ResolveEndpoint(string baseUrl, string route) {
        var trimmed = baseUrl.TrimEnd('/');
        return trimmed.EndsWith(route, StringComparison.OrdinalIgnoreCase)
            ? new Uri(trimmed)
            : new Uri(trimmed + route);
    }

    private static string? ReadError(string body) {
        if (body.Length == 0) {
            return null;
        }

        try {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) &&
                   error.TryGetProperty("message", out var message)
                ? message.GetString()
                : null;
        } catch (JsonException) {
            return null;
        }
    }
}
using System.Net.Http.Headers;
using System.Text.Json;

namespace Kite.Agent;

internal abstract class AgentClient(string apiKey, string baseUrl, string route, string providerId) : IDisposable {
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly Uri _endpoint = ResolveEndpoint(baseUrl, route);

    public abstract string DisplayName { get; }

    public void Dispose() => _http.Dispose();

    public abstract Task<AgentReply> StreamReplyAsync(IReadOnlyList<ConversationMessage> conversation, string sessionId,
        Func<AgentEvent, Task> onEvent,
        Func<IReadOnlyList<ToolCall>, CancellationToken, Task<IReadOnlyList<string>>>? executeToolCalls,
        CancellationToken ct);

    protected HttpRequestMessage CreateRequest(ReadOnlyMemory<byte> body, string sessionId) {
        var content = new ReadOnlyMemoryContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.UserAgent.ParseAdd("kite/1.0");

        if (providerId == "opencode-go") {
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
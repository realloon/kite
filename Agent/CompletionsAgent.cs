using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kite.Configuration;
using Kite.Tools;

namespace Kite.Agent;

/// <summary>Agent backed by an OpenAI Completions API-compatible endpoint.</summary>
public sealed class CompletionsAgent(
    string apiKey,
    string baseUrl,
    string model,
    string? instructions,
    int? maxTokens,
    string providerId)
    : IAgent {
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly Uri _endpoint = ResolveEndpoint(baseUrl);
    private readonly bool _isOpenCodeGo = providerId == "opencode-go";
    private readonly string _instructions = instructions ?? string.Empty;

    private static Uri ResolveEndpoint(string baseUrl) {
        var trimmed = baseUrl.TrimEnd('/');
        return trimmed.EndsWith("/completions", StringComparison.OrdinalIgnoreCase)
            ? new Uri(trimmed)
            : new Uri(trimmed + "/completions");
    }

    public string DisplayName => model;

    public static CompletionsAgent Create(string apiKey, ModelPreset model, string? variant, string? instructions) {
        if (variant is not null) {
            throw new InvalidOperationException($"Completions model '{model.Id}' does not support variants");
        }

        if (model.Tools.Count > 0) {
            throw new InvalidOperationException($"Completions model '{model.Id}' does not support tools");
        }

        return new CompletionsAgent(
            apiKey,
            model.BaseUrl ?? throw new InvalidOperationException($"Model '{model.Id}' has no baseUrl"),
            model.Id,
            instructions,
            model.Limit?.Output,
            model.ProviderId);
    }

    public async Task<AgentReply> StreamReplyAsync(
        IReadOnlyList<ConversationMessage> conversation,
        string sessionId,
        Func<AgentEvent, Task> onEvent,
        Func<IReadOnlyList<ToolCall>, CancellationToken, Task<IReadOnlyList<string>>>? executeToolCalls,
        CancellationToken cancellationToken) {
        var prompt = BuildPrompt(conversation);
        var request = new CompletionsRequest {
            Model = model,
            Prompt = prompt,
            Stream = true,
            MaxTokens = maxTokens
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(request, KiteJsonContext.Default.CompletionsRequest);
        using var content = new ReadOnlyMemoryContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        httpRequest.Content = content;
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");
        httpRequest.Headers.UserAgent.ParseAdd("kite/1.0");
        if (_isOpenCodeGo) {
            httpRequest.Headers.TryAddWithoutValidation("x-opencode-session", sessionId);
        }

        using var response =
            await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(TryReadErrorMessage(body) ?? $"HTTP {(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var promptTokens = 0;
        var completionTokens = 0;
        var done = false;
        try {
            while (await reader.ReadLineAsync(cancellationToken) is { } line) {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var payload = line.AsSpan(5).Trim();
                if (payload.IsEmpty) continue;

                if (payload.SequenceEqual("[DONE]".AsSpan())) {
                    done = true;
                    break;
                }

                using var document = JsonDocument.Parse(payload.ToString());
                var root = document.RootElement;
                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0) {
                    var choice = choices[0];
                    if (choice.TryGetProperty("text", out var text)) {
                        var delta = text.GetString() ?? string.Empty;
                        if (delta.Length > 0) {
                            await onEvent(AgentEvent.TextDelta(delta));
                        }
                    }
                }

                if (!root.TryGetProperty("usage", out var usage)) continue;

                promptTokens = ReadInt(usage, "prompt_tokens");
                completionTokens = ReadInt(usage, "completion_tokens");
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            return new AgentReply(promptTokens, completionTokens);
        } catch (JsonException ex) {
            throw new InvalidOperationException("Completion stream contains invalid JSON", ex);
        }

        if (!done && !cancellationToken.IsCancellationRequested) {
            throw new InvalidOperationException("Completion stream ended before [DONE]");
        }

        return new AgentReply(promptTokens, completionTokens);
    }

    public void Dispose() => _http.Dispose();

    private string BuildPrompt(IReadOnlyList<ConversationMessage> conversation) {
        var builder = new StringBuilder();
        if (_instructions.Length > 0) {
            builder.AppendLine(_instructions);
            builder.AppendLine();
        }

        foreach (var message in conversation) {
            if (message.Type is ConversationMessage.FunctionCallType or ConversationMessage.FunctionCallOutputType) {
                continue;
            }

            builder.Append(message.Role switch {
                "assistant" => "Assistant",
                "system" => "System",
                _ => "User"
            });
            builder.Append(": ");
            builder.AppendLine(message.Content);
        }

        builder.Append("Assistant: ");
        return builder.ToString();
    }

    private static int ReadInt(JsonElement value, string property) =>
        value.TryGetProperty(property, out var element) && element.TryGetInt32(out var result) ? result : 0;

    private static string? TryReadErrorMessage(string body) {
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

    internal sealed class CompletionsRequest {
        public string Model { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public bool Stream { get; set; }

        [JsonPropertyName("max_tokens")]
        public int? MaxTokens { get; set; }
    }
}
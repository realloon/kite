using System.Text.Json;
using Kite.Agent;
using Kite.Ui;

namespace Kite;

public sealed class KiteApp(IAgent? agent, IChatView view, KiteConfig? config = null) : IDisposable {
    private readonly KiteConfig _config = config ?? new KiteConfig();
    private IAgent? _agent = agent;
    private readonly List<ConversationMessage> _conversation = [];

    public async Task<int> RunAsync(CancellationToken cancellationToken) {
        if (_agent is ResponsesAgent responses) {
            responses.ExecuteToolCall ??= ExecuteToolCallAsync;
        }

        view.ShowWelcome();
        if (_agent is null) {
            view.WriteInfo("No API key. Enter /connect to add one.");
        }

        while (!cancellationToken.IsCancellationRequested) {
            var input = await view.ReadUserInputAsync(cancellationToken);
            if (input is null || SlashCommands.Find(input)?.Name == "/exit") break;

            if (input.StartsWith('/')) {
                await HandleSlashAsync(input, cancellationToken);
                continue;
            }

            if (string.IsNullOrWhiteSpace(input)) continue;

            if (_agent is null) {
                view.WriteError("No API key. Run /connect first.");
                continue;
            }

            _conversation.Add(ConversationMessage.User(input));
            view.AddUserMessage(input);
            view.StartAssistantTurn();

            var interrupted = false;
            string? error = null;
            var reply = AgentReply.Empty;
            try {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    view.TurnCancellationToken);
                reply = await _agent.StreamReplyAsync(
                    _conversation,
                    agentEvent => {
                        switch (agentEvent.Kind) {
                            case AgentEventKind.TextDelta:
                                view.AppendAssistantChunk(agentEvent.Text);
                                break;
                            case AgentEventKind.ReasoningDelta:
                                view.AppendReasoningChunk(agentEvent.Text);
                                break;
                            default:
                                throw new InvalidOperationException(
                                    $"Unhandled agent event: {agentEvent.Kind}");
                        }

                        return Task.CompletedTask;
                    },
                    linked.Token);
            } catch (OperationCanceledException) when (view.TurnCancellationToken.IsCancellationRequested) {
                interrupted = true;
            } catch (Exception ex) {
                error = ex.Message;
            }

            interrupted |= view.TurnCancellationToken.IsCancellationRequested;
            await view.EndAssistantTurnAsync(interrupted, reply.PromptTokens, reply.CompletionTokens);

            if (error is not null) {
                view.WriteError(error);
            } else if (reply.Text.Length > 0) {
                _conversation.Add(ConversationMessage.Assistant(reply.Text));
            }
        }

        return 0;
    }

    private async Task HandleSlashAsync(string input, CancellationToken cancellationToken) {
        switch (SlashCommands.Find(input)?.Name) {
            case "/connect":
                await ConnectDeepSeekAsync(cancellationToken);
                break;
            case "/variants":
                await ChangeVariantAsync(cancellationToken);
                break;
            case "/new":
                _conversation.Clear();
                view.ResetTranscript();
                break;
            default:
                view.WriteError(
                    $"Unknown command: {input} (available: {string.Join(", ", SlashCommands.All.Select(command => command.Name))})");
                break;
        }
    }

    /// <summary>
    /// /connect: prompt for a DeepSeek API key (masked input), persist it,
    /// and hot-swap the agent.
    /// </summary>
    private async Task ConnectDeepSeekAsync(CancellationToken cancellationToken) {
        var prompt = _config.HasDeepSeekKey
            ? "Replace the DeepSeek API key (Enter to keep the current key):"
            : "DeepSeek API key (Enter to submit, Ctrl+C to cancel):";
        var key = await view.ReadSecretAsync(prompt, cancellationToken);
        if (key is null) {
            view.WriteInfo("Connection cancelled.");
            return;
        }

        if (string.IsNullOrWhiteSpace(key)) {
            if (_config.HasDeepSeekKey && _agent is not null) {
                view.WriteInfo($"Keeping current config: DeepSeek · {_agent.DisplayName}");
                return;
            }

            view.WriteError("No API key entered. Connection cancelled.");
            return;
        }

        try {
            var newAgent = AgentFactory.CreateDeepSeek(key.Trim(), config: _config);
            (_agent as IDisposable)?.Dispose();
            _agent = newAgent;
            _config.Provider = "deepseek";
            _config.ApiKey = key.Trim();
            _config.Save();
            view.SetModelName(_agent.DisplayName);
            view.WriteInfo($"Connected to DeepSeek · {_agent.DisplayName}. Ready.");
        } catch (Exception ex) {
            view.WriteError($"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// /variants: pick a reasoning.effort from the model's preset list, then
    /// hot-swap the agent and persist it.
    /// </summary>
    private async Task ChangeVariantAsync(CancellationToken cancellationToken) {
        if (_agent is null) {
            view.WriteError("Run /connect first.");
            return;
        }

        var key = _config.ApiKey;
        if (string.IsNullOrEmpty(key)) {
            view.WriteError("No API key. Run /connect first.");
            return;
        }

        var variants = ModelCatalog.Find(_agent.ModelName)?.Variants;
        if (variants is not { Count: > 0 }) {
            view.WriteError($"No reasoning effort options available for {_agent.ModelName}.");
            return;
        }

        var value = await view.ReadChoiceAsync(
            "Reasoning effort:", variants, cancellationToken);
        if (value is null) {
            return;
        }

        try {
            var newAgent = AgentFactory.CreateDeepSeek(key, reasoningEffort: value, config: _config);
            (_agent as IDisposable)?.Dispose();
            _agent = newAgent;
            _config.Variants = value;
            _config.Save();
            view.SetModelName(_agent.DisplayName);
            view.WriteInfo($"Changed to: {_agent.DisplayName}");
        } catch (Exception ex) {
            view.WriteError($"Change failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Tool executor for the run tool: no sandbox, no confirmation, no limits.
    /// </summary>
    private async Task<string> ExecuteToolCallAsync(ToolCall call, CancellationToken cancellationToken) {
        if (!string.Equals(call.Name, RunBash.DefaultName, StringComparison.Ordinal)) {
            return $"unknown tool: {call.Name}";
        }

        var command = TryReadCommand(call.Arguments) ?? call.Arguments;
        view.AppendToolLine($"{RunBash.DefaultName} {command}");

        try {
            return await RunBash.RunAsync(command, cancellationToken);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            return $"error: {ex.Message}";
        }
    }

    private static string? TryReadCommand(string arguments) {
        try {
            using var doc = JsonDocument.Parse(arguments);
            if (doc.RootElement.TryGetProperty("command", out var command) &&
                command.ValueKind == JsonValueKind.String) {
                return command.GetString();
            }
        } catch (JsonException) {
            // Not JSON: pass the raw text through
        }

        return null;
    }

    public void Dispose() => (_agent as IDisposable)?.Dispose();
}
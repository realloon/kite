using System.Runtime.ExceptionServices;
using System.Text.Json;
using Kite.Agent;
using Kite.Commands;
using Kite.Configuration;
using Kite.Sessions;
using Kite.Tools;
using Kite.Ui;

namespace Kite.App;

public sealed class KiteApp : IDisposable {
    private readonly KiteConfig _config;
    private readonly IChatView _view;
    private readonly SessionStore _store;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SessionThread> _threads = [];
    private IAgent? _agent;
    private SessionThread _activeThread;
    private bool _stopping;
    private bool _disposed;

    public KiteApp(
        IAgent? agent,
        IChatView view,
        KiteConfig? config = null,
        SessionStore? store = null) {
        _agent = agent;
        _view = view;
        _config = config ?? new KiteConfig();
        _store = store ?? new SessionStore(Directory.GetCurrentDirectory());
        var sessions = _store.List();
        if (sessions.Count == 0) {
            sessions = [_store.Create()];
        }

        foreach (var session in sessions) {
            AddThread(SessionThread.Open(session));
        }

        _activeThread = _threads[sessions[0].Id];
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken) {
        try {
            _view.ShowWelcome();
            lock (_gate) {
                _view.LoadTranscript(_activeThread.Snapshot(), _activeThread.IsStreaming);
                if (_agent is null) {
                    _view.WriteInfo("No API key. Enter /connect to add one.");
                }
            }

            while (!cancellationToken.IsCancellationRequested) {
                var input = await _view.ReadUserInputAsync(cancellationToken, CancelActiveTurn);
                if (input is null || SlashCommands.Find(input)?.Name == "/exit") {
                    break;
                }

                if (input.StartsWith('/')) {
                    await HandleSlashAsync(input, cancellationToken);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(input)) {
                    StartTurn(input, cancellationToken);
                }
            }
        } finally {
            await StopThreadsAsync();
        }

        return 0;
    }

    private SessionThread AddThread(SessionThread thread) {
        _threads.Add(thread.Session.Id, thread);
        return thread;
    }

    private void StartTurn(string input, CancellationToken cancellationToken) {
        lock (_gate) {
            if (_agent is null) {
                _view.WriteError("No API key. Run /connect first.");
                return;
            }

            if (_activeThread.IsStreaming) {
                _view.WriteError("This session is still generating. Use /sessions to switch.");
                return;
            }

            var thread = _activeThread;
            var agent = _agent;
            _store.Append(thread.Session, [ConversationMessage.User(input)]);
            thread.AddUserMessage(input);
            thread.StartTurn();
            _view.AddUserMessage(input);
            _view.StartAssistantTurn();

            var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            thread.TurnCancellation = turnCancellation;
            thread.TurnTask = RunTurnAsync(thread, agent, turnCancellation);
        }
    }

    private bool CancelActiveTurn() {
        lock (_gate) {
            var cancellation = _activeThread.TurnCancellation;
            if (!_activeThread.IsStreaming || cancellation is null) return false;

            cancellation.Cancel();
            return true;
        }
    }

    private async Task RunTurnAsync(
        SessionThread thread,
        IAgent agent,
        CancellationTokenSource turnCancellation) {
        var reply = AgentReply.Empty;
        Exception? failure = null;
        Exception? saveException = null;
        var rethrowSaveException = false;

        try {
            IReadOnlyList<ConversationMessage> conversation;
            lock (_gate) {
                conversation = [.. thread.Messages];
            }

            reply = await agent.StreamReplyAsync(
                conversation,
                agentEvent => HandleAgentEventAsync(thread, agentEvent),
                (call, cancellationToken) => ExecuteToolCallAsync(thread, call, cancellationToken),
                turnCancellation.Token);
        } catch (OperationCanceledException) when (turnCancellation.IsCancellationRequested) { } catch (Exception ex) {
            failure = ex;
        } finally {
            try {
                lock (_gate) {
                    var interrupted = turnCancellation.IsCancellationRequested;
                    string? saveFailure = null;
                    try {
                        var assistantText = thread.CurrentAssistantText;
                        if (assistantText.Length > 0) {
                            _store.Append(thread.Session, [ConversationMessage.Assistant(assistantText)]);
                        }
                    } catch (Exception ex) {
                        saveFailure = $"Could not save session: {ErrorMessage(ex)}";
                        thread.AddError(saveFailure);
                        saveException = ex;
                        rethrowSaveException = _stopping;
                    }

                    var duration = thread.CompleteTurn();
                    var meta = new TurnMeta(
                        duration,
                        reply.PromptTokens,
                        reply.CompletionTokens,
                        interrupted);
                    var status = interrupted ? $"interrupted — {meta}" : meta.ToString();
                    thread.AddInfo(status);
                    if (failure is not null) {
                        thread.AddError(ErrorMessage(failure));
                    }

                    if (!_stopping && ReferenceEquals(_activeThread, thread)) {
                        _view.EndAssistantTurn();
                        _view.WriteInfo(status);
                        if (failure is not null) {
                            _view.WriteError(ErrorMessage(failure));
                        }

                        if (saveFailure is not null) {
                            _view.WriteError(saveFailure);
                        }
                    }
                }
            } finally {
                lock (_gate) {
                    if (ReferenceEquals(thread.TurnCancellation, turnCancellation)) {
                        thread.TurnCancellation = null;
                    }
                }

                turnCancellation.Dispose();
            }
        }

        if (rethrowSaveException && saveException is not null) {
            ExceptionDispatchInfo.Capture(saveException).Throw();
        }
    }

    private Task HandleAgentEventAsync(SessionThread thread, AgentEvent agentEvent) {
        lock (_gate) {
            switch (agentEvent.Kind) {
                case AgentEventKind.TextDelta:
                    thread.AppendAssistant(agentEvent.Text);
                    if (!_stopping && ReferenceEquals(_activeThread, thread)) {
                        _view.AppendAssistantChunk(agentEvent.Text);
                    }

                    break;
                case AgentEventKind.ReasoningDelta:
                    thread.AppendReasoning(agentEvent.Text);
                    if (!_stopping && ReferenceEquals(_activeThread, thread)) {
                        _view.AppendReasoningChunk(agentEvent.Text);
                    }

                    break;
                default:
                    throw new InvalidOperationException($"Unhandled agent event: {agentEvent.Kind}");
            }
        }

        return Task.CompletedTask;
    }

    private async Task<string> ExecuteToolCallAsync(
        SessionThread thread,
        ToolCall call,
        CancellationToken cancellationToken) {
        var description = call.Name;
        lock (_gate) {
            var assistantText = thread.CurrentAssistantText;
            if (assistantText.Length > 0) {
                _store.Append(thread.Session, [ConversationMessage.Assistant(assistantText)]);
            }

            thread.AddTool(description);
            if (!_stopping && ReferenceEquals(_activeThread, thread)) {
                _view.AppendToolLine(description);
            }
        }

        string output;
        try {
            if (string.Equals(call.Name, RunBash.DefaultName, StringComparison.Ordinal)) {
                output = await RunBash.RunAsync(
                    ReadCommand(call.Arguments), thread.Session.Workspace, cancellationToken);
            } else {
                output = FileTools.Execute(call, thread.Session.Workspace, cancellationToken);
            }
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            output = $"error: {ErrorMessage(ex)}";
        }

        lock (_gate) {
            _store.Append(thread.Session, [
                ConversationMessage.FunctionCall(call),
                ConversationMessage.FunctionCallOutput(call.Id, output)
            ]);
        }

        return output;
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
                NewSession();
                break;
            case "/sessions":
                await SwitchSessionAsync(cancellationToken);
                break;
            default:
                _view.WriteError(
                    $"Unknown command: {input} (available: {string.Join(", ", SlashCommands.All.Select(command => command.Name))})");
                break;
        }
    }

    private void NewSession() {
        lock (_gate) {
            _activeThread = AddThread(SessionThread.Open(_store.Create()));
            _view.LoadTranscript(_activeThread.Snapshot(), streaming: false);
        }
    }

    private async Task SwitchSessionAsync(CancellationToken cancellationToken) {
        while (true) {
            SessionThread[] threads;
            string[] choices;
            lock (_gate) {
                threads = [.. _threads.Values.OrderByDescending(thread => thread.Session.UpdatedAt)];
                choices = [
                    .. threads
                        .Select(thread => {
                            var label = SessionStore.Label(
                                thread.Session,
                                ReferenceEquals(thread, _activeThread));
                            return thread.IsStreaming ? $"{label} · running" : label;
                        })
                ];
            }

            var result = await _view.ReadChoiceAsync(
                "Sessions:",
                choices,
                cancellationToken,
                allowDelete: true);
            if (result is null) return;
            if (result.Index < 0 || result.Index >= threads.Length) {
                throw new InvalidOperationException("The selected session no longer exists");
            }

            if (result.DeleteRequested) {
                DeleteSession(threads[result.Index]);
                continue;
            }

            lock (_gate) {
                _activeThread = threads[result.Index];
                _view.LoadTranscript(_activeThread.Snapshot(), _activeThread.IsStreaming);
            }

            return;
        }
    }

    private void DeleteSession(SessionThread thread) {
        lock (_gate) {
            if (thread.IsStreaming) {
                _view.WriteError("Cannot delete a running session.");
                return;
            }

            _store.Delete(thread.Session);
            if (!_threads.Remove(thread.Session.Id)) {
                throw new InvalidOperationException("The selected session no longer exists");
            }

            if (!ReferenceEquals(thread, _activeThread)) return;

            _activeThread = _threads.Values
                                .OrderByDescending(candidate => candidate.Session.UpdatedAt)
                                .FirstOrDefault()
                            ?? AddThread(SessionThread.Open(_store.Create()));
            _view.LoadTranscript(_activeThread.Snapshot(), _activeThread.IsStreaming);
        }
    }

    private async Task ConnectDeepSeekAsync(CancellationToken cancellationToken) {
        if (HasStreamingThreads()) {
            _view.WriteError("Stop active sessions before changing the connection.");
            return;
        }

        var prompt = _config.HasDeepSeekKey
            ? "Replace the DeepSeek API key (Enter to keep the current key):"
            : "DeepSeek API key (Enter to submit, Ctrl+C to cancel):";
        var key = await _view.ReadSecretAsync(prompt, cancellationToken);
        if (key is null) {
            _view.WriteInfo("Connection cancelled.");
            return;
        }

        if (string.IsNullOrWhiteSpace(key)) {
            if (_config.HasDeepSeekKey && _agent is not null) {
                _view.WriteInfo($"Keeping current config: DeepSeek · {_agent.DisplayName}");
                return;
            }

            _view.WriteError("No API key entered. Connection cancelled.");
            return;
        }

        ResponsesAgent? newAgent = null;
        var previousProvider = _config.Provider;
        var previousApiKey = _config.ApiKey;
        try {
            var trimmedKey = key.Trim();
            newAgent = AgentFactory.CreateDeepSeek(trimmedKey, config: _config);
            _config.Provider = "deepseek";
            _config.ApiKey = trimmedKey;
            _config.Save();

            IAgent? oldAgent;
            lock (_gate) {
                oldAgent = _agent;
                _agent = newAgent;
                _view.SetModelName(newAgent.DisplayName);
            }

            newAgent = null;
            DisposePreviousAgent(oldAgent);

            _view.WriteInfo($"Connected to DeepSeek · {_agent!.DisplayName}. Ready.");
        } catch (Exception ex) {
            _config.Provider = previousProvider;
            _config.ApiKey = previousApiKey;
            newAgent?.Dispose();
            _view.WriteError($"Connection failed: {ex.Message}");
        }
    }

    private async Task ChangeVariantAsync(CancellationToken cancellationToken) {
        if (HasStreamingThreads()) {
            _view.WriteError("Stop active sessions before changing the variant.");
            return;
        }

        IAgent? currentAgent;
        lock (_gate) {
            currentAgent = _agent;
        }

        if (currentAgent is null) {
            _view.WriteError("Run /connect first.");
            return;
        }

        var key = _config.ApiKey;
        if (string.IsNullOrEmpty(key)) {
            _view.WriteError("No API key. Run /connect first.");
            return;
        }

        var variants = ModelCatalog.Find(currentAgent.ModelName)?.Variants;
        if (variants is not { Count: > 0 }) {
            _view.WriteError($"No reasoning effort options available for {currentAgent.ModelName}.");
            return;
        }

        var result = await _view.ReadChoiceAsync("Reasoning effort:", variants, cancellationToken);
        if (result is null) return;
        if (result.Index < 0 || result.Index >= variants.Count) {
            throw new InvalidOperationException("The selected variant no longer exists");
        }

        var value = variants[result.Index];

        ResponsesAgent? newAgent = null;
        var previousVariants = _config.Variants;
        try {
            newAgent = AgentFactory.CreateDeepSeek(key, reasoningEffort: value, config: _config);
            _config.Variants = value;
            _config.Save();

            IAgent? oldAgent;
            lock (_gate) {
                oldAgent = _agent;
                _agent = newAgent;
                _view.SetModelName(newAgent.DisplayName);
            }

            newAgent = null;
            DisposePreviousAgent(oldAgent);

            _view.WriteInfo($"Changed to: {_agent!.DisplayName}");
        } catch (Exception ex) {
            _config.Variants = previousVariants;
            newAgent?.Dispose();
            _view.WriteError($"Change failed: {ex.Message}");
        }
    }

    private bool HasStreamingThreads() {
        lock (_gate) {
            return _threads.Values.Any(thread => thread.IsStreaming);
        }
    }

    private void DisposePreviousAgent(IAgent? agent) {
        try {
            (agent as IDisposable)?.Dispose();
        } catch (Exception ex) {
            _view.WriteError($"Previous connection cleanup failed: {ErrorMessage(ex)}");
        }
    }

    private async Task StopThreadsAsync() {
        Task[] tasks;
        lock (_gate) {
            _stopping = true;
            foreach (var thread in _threads.Values) {
                thread.TurnCancellation?.Cancel();
            }

            tasks = [
                .. _threads.Values
                    .Select(thread => thread.TurnTask)
                    .OfType<Task>()
                    .Where(task => !task.IsCompleted)
            ];
        }

        if (tasks.Length > 0) {
            await Task.WhenAll(tasks);
        }
    }

    public void Dispose() {
        lock (_gate) {
            if (_disposed) return;

            _disposed = true;
        }

        StopThreadsAsync().GetAwaiter().GetResult();
        (_agent as IDisposable)?.Dispose();
    }

    private static string ErrorMessage(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;

    private static string ReadCommand(string arguments) {
        try {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("command", out var command) &&
                command.ValueKind == JsonValueKind.String) {
                return command.GetString()
                       ?? throw new InvalidOperationException("Tool command is null");
            }
        } catch (JsonException ex) {
            throw new InvalidOperationException("Tool arguments are invalid JSON", ex);
        }

        throw new InvalidOperationException("Tool arguments do not contain a string command");
    }
}
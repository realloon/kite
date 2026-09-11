using System.Runtime.ExceptionServices;
using System.Text.Json;
using Kite.Agent;
using Kite.Commands;
using Kite.Configuration;
using Kite.Context;
using Kite.Sessions;
using Kite.Tools;
using Kite.Ui;

namespace Kite.App;

public sealed class KiteApp : IDisposable {
    private readonly ModelCatalog _catalog;
    private readonly KiteAuth _auth;
    private readonly KiteState _state;
    private readonly FullScreenChatView _view;
    private readonly SessionStore _store;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SessionThread> _threads = [];
    private ResponsesAgent? _agent;
    private SessionThread _activeThread;
    private bool _stopping;
    private bool _disposed;

    public KiteApp(ResponsesAgent? agent, FullScreenChatView view, ModelCatalog catalog, KiteAuth auth, KiteState state,
        SessionStore store) {
        _agent = agent;
        _view = view;
        _catalog = catalog;
        _auth = auth;
        _state = state;
        _store = store;
        var sessions = _store.List();
        if (sessions.Count == 0) {
            sessions = [_store.Create()];
        }

        foreach (var session in sessions) {
            var thread = SessionThread.Open(session);
            AddThread(thread);
        }

        _activeThread = _threads[sessions[0].Id];
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken) {
        try {
            _view.ShowWelcome();
            lock (_gate) {
                _view.LoadTranscript(_activeThread.Snapshot(), _activeThread.IsStreaming);
                RefreshSessionCost(_activeThread);
                if (_agent is null) {
                    _view.WriteInfo("Not connected. Run /connect to add a key; use /model to change the model.");
                }
            }

            while (!cancellationToken.IsCancellationRequested) {
                var input = await _view.ReadUserInputAsync(CancelActiveTurn, cancellationToken);
                if (input is null || SlashCommands.Find(input)?.Name == "/exit") break;

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
            if (!_activeThread.IsStreaming || cancellation is null) {
                return false;
            }

            cancellation.Cancel();
            return true;
        }
    }

    private async Task RunTurnAsync(
        SessionThread thread,
        ResponsesAgent agent,
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
                thread.Session.Id,
                agentEvent => HandleAgentEventAsync(thread, agentEvent),
                (calls, cancellationToken) => ExecuteToolCallsAsync(
                    thread, calls, cancellationToken),
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
                    var model = _catalog.FindModel(_state.Provider, _state.Model);
                    if (model?.Cost?.Peak is { Input: { } input, Output: { } output }) {
                        thread.Session.Cost +=
                            (decimal)(reply.PromptTokens * input + reply.CompletionTokens * output) /
                            1_000_000m;
                        _store.SaveCost(thread.Session);
                        if (!_stopping && ReferenceEquals(_activeThread, thread)) {
                            _view.SetSessionCost($"{model.Cost.Currency}{thread.Session.Cost:0.00}");
                        }
                    }

                    var status =
                        $"{(interrupted ? "interrupted — " : "")}{duration.TotalSeconds:F1}s (↑{reply.PromptTokens} ↓{reply.CompletionTokens}{(interrupted ? " ⏹" : "")})";
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

    private Task HandleAgentEventAsync(
        SessionThread thread,
        AgentEvent agentEvent) {
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

    private async Task<IReadOnlyList<string>> ExecuteToolCallsAsync(
        SessionThread thread,
        IReadOnlyList<ToolCall> calls,
        CancellationToken cancellationToken) {
        lock (_gate) {
            var assistantText = thread.CurrentAssistantText;
            if (assistantText.Length > 0) {
                _store.Append(thread.Session, [ConversationMessage.Assistant(assistantText)]);
            }

            foreach (var call in calls) {
                thread.AddTool(call.Preview);
                if (!_stopping && ReferenceEquals(_activeThread, thread)) {
                    _view.AppendToolLine(call.Preview);
                }
            }
        }

        // File tools are synchronous; offload each call so independent tools overlap.
        var tasks = calls
            .Select(call => Task.Run(() => ExecuteToolCallAsync(thread, call, cancellationToken), cancellationToken))
            .ToArray();
        var outputs = await Task.WhenAll(tasks);

        lock (_gate) {
            var messages = new List<ConversationMessage>(calls.Count * 2);
            messages.AddRange(calls.Select(ConversationMessage.FunctionCall));
            messages.AddRange(calls.Select((call, index) =>
                ConversationMessage.FunctionCallOutput(call.Id, outputs[index])));
            _store.Append(thread.Session, messages);
        }

        return outputs;
    }

    private async Task<string> ExecuteToolCallAsync(
        SessionThread thread,
        ToolCall call,
        CancellationToken cancellationToken) {
        try {
            return string.Equals(call.Name, RunShell.DefaultName, StringComparison.Ordinal)
                ? await RunShell.RunAsync(ReadCommand(call.Arguments), thread.Session.Workspace, cancellationToken)
                : FileTools.Execute(call, thread.Session.Workspace, cancellationToken);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            return $"error: {ErrorMessage(ex)}";
        }
    }

    private async Task HandleSlashAsync(string input, CancellationToken cancellationToken) {
        switch (SlashCommands.Find(input)?.Name) {
            case "/connect":
                await ConnectAsync(cancellationToken);
                break;
            case "/model":
                await ChangeModelAsync(cancellationToken);
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
                _view.WriteError("Unknown command.");
                break;
        }
    }

    private void NewSession() {
        lock (_gate) {
            _activeThread = AddThread(SessionThread.Open(_store.Create()));
            _view.LoadTranscript(_activeThread.Snapshot(), streaming: false);
            RefreshSessionCost(_activeThread);
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

            var result = await _view.ReadChoiceAsync(choices, cancellationToken, true);
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
                RefreshSessionCost(_activeThread);
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

            _activeThread = _threads.Values.OrderByDescending(candidate => candidate.Session.UpdatedAt).FirstOrDefault()
                            ?? AddThread(SessionThread.Open(_store.Create()));
            _view.LoadTranscript(_activeThread.Snapshot(), _activeThread.IsStreaming);
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken) {
        if (HasStreamingThreads()) {
            _view.WriteError("Stop active sessions before changing the connection.");
            return;
        }

        var provider = await SelectProviderAsync(cancellationToken);
        if (provider is null) return;

        var currentKey = _auth.Get(provider.Id);
        var prompt = currentKey is null
            ? $"{provider.Id} API key (Enter to submit, Ctrl+C to cancel):"
            : $"Replace the {provider.Id} API key (Enter to keep the current key):";
        var key = await _view.ReadSecretAsync(prompt, cancellationToken);
        if (key is null) {
            _view.WriteInfo("Connection cancelled.");
            return;
        }

        var trimmedKey = string.IsNullOrWhiteSpace(key) ? currentKey : key.Trim();
        if (trimmedKey is null) {
            _view.WriteError("No API key entered. Connection cancelled.");
            return;
        }

        _auth.Set(provider.Id, trimmedKey);
        _auth.Save();

        if (_state.Provider is not null &&
            string.Equals(provider.Id, _state.Provider, StringComparison.OrdinalIgnoreCase)) {
            var model = _state.Model is null
                ? null
                : provider.Models.FirstOrDefault(m =>
                    string.Equals(m.Id, _state.Model, StringComparison.OrdinalIgnoreCase));
            if (model is not null) {
                var variant = _state.Variant;
                if (model.Variants.Count > 0 && variant is null) {
                    variant = await SelectVariantForModelAsync(model, cancellationToken);
                    if (variant is null) {
                        _view.WriteInfo("Connection cancelled.");
                        return;
                    }

                    _state.Variant = variant;
                    _state.Save();
                }

                ResponsesAgent? newAgent = null;
                try {
                    newAgent = CreateAgent(trimmedKey, model, variant);
                    ActivateAgent(newAgent);
                    _view.WriteInfo($"Connected to {provider.Id} · {_agent!.DisplayName}. Ready.");
                    return;
                } catch (Exception ex) {
                    newAgent?.Dispose();
                    _view.WriteError($"Connection refresh failed: {ex.Message}");
                    return;
                }
            }
        }

        if (_state.Provider is not null) {
            _view.WriteInfo($"Saved API key for {provider.Id}. Use /model to switch models.");
            return;
        }

        var selectedModel = await SelectModelForProviderAsync(provider, cancellationToken);
        if (selectedModel is null) {
            _view.WriteInfo("Connection cancelled.");
            return;
        }

        string? selectedVariant = null;
        if (selectedModel.Variants.Count > 0) {
            selectedVariant = await SelectVariantForModelAsync(selectedModel, cancellationToken);
            if (selectedVariant is null) {
                _view.WriteInfo("Connection cancelled.");
                return;
            }
        }

        ResponsesAgent? agent = null;
        try {
            agent = CreateAgent(trimmedKey, selectedModel, selectedVariant);
            _state.Provider = provider.Id;
            _state.Model = selectedModel.Id;
            _state.Variant = selectedVariant;
            _state.Save();

            ActivateAgent(agent);
            _view.WriteInfo($"Connected to {provider.Id} · {_agent!.DisplayName}. Ready.");
        } catch (Exception ex) {
            agent?.Dispose();
            _view.WriteError($"Connection failed: {ex.Message}");
        }
    }

    private async Task<ProviderPreset?> SelectProviderAsync(CancellationToken cancellationToken) {
        if (_catalog.Providers.Count == 1) {
            return _catalog.Providers[0];
        }

        var choices = _catalog.Providers
            .Select(p => {
                var isCurrent = string.Equals(p.Id, _state.Provider, StringComparison.OrdinalIgnoreCase);
                return $"{(isCurrent ? "* " : "  ")}{p.Id}";
            })
            .ToArray();
        var result = await _view.ReadChoiceAsync(choices, cancellationToken);
        if (result is null) return null;

        if (result.Index < 0 || result.Index >= _catalog.Providers.Count) {
            throw new InvalidOperationException("The selected provider no longer exists");
        }

        return _catalog.Providers[result.Index];
    }

    private async Task<ModelPreset?> SelectModelForProviderAsync(
        ProviderPreset provider,
        CancellationToken cancellationToken) {
        var choices = provider.Models
            .Select(model => {
                var selected = string.Equals(provider.Id, _state.Provider, StringComparison.OrdinalIgnoreCase)
                               && string.Equals(model.Id, _state.Model, StringComparison.OrdinalIgnoreCase);
                return $"{(selected ? "* " : "  ")}{model.Id}";
            })
            .ToArray();
        var result = await _view.ReadChoiceAsync(choices, cancellationToken);
        if (result is null) return null;

        if (result.Index < 0 || result.Index >= provider.Models.Count) {
            throw new InvalidOperationException("The selected model no longer exists");
        }

        return provider.Models[result.Index];
    }

    private async Task<string?> SelectVariantForModelAsync(
        ModelPreset model,
        CancellationToken cancellationToken) {
        if (model.Variants.Count == 0) return null;

        var choices = model.Variants
            .Select(variant => {
                var selected = _state.Variant is not null
                               && string.Equals(variant, _state.Variant, StringComparison.OrdinalIgnoreCase);
                return $"{(selected ? "* " : "  ")}{variant}";
            })
            .ToArray();
        var result = await _view.ReadChoiceAsync(choices, cancellationToken);
        if (result is null) return null;

        if (result.Index < 0 || result.Index >= model.Variants.Count) {
            throw new InvalidOperationException("The selected variant no longer exists");
        }

        return model.Variants[result.Index];
    }

    private async Task ChangeModelAsync(CancellationToken cancellationToken) {
        if (HasStreamingThreads()) {
            _view.WriteError("Stop active sessions before changing the model.");
            return;
        }

        var models = _catalog.Models
            .Where(selection => _auth.Get(selection.Provider.Id) is not null)
            .ToArray();
        if (models.Length == 0) {
            _view.WriteError("No configured models. Run /connect first.");
            return;
        }

        var choices = models
            .Select(selection => {
                var selected = string.Equals(selection.Provider.Id, _state.Provider, StringComparison.OrdinalIgnoreCase)
                               && string.Equals(selection.Model.Id, _state.Model, StringComparison.OrdinalIgnoreCase);
                return $"{(selected ? "* " : "  ")}{selection.Provider.Id} / {selection.Model.Id}";
            })
            .ToArray();
        var result = await _view.ReadChoiceAsync(choices, cancellationToken);
        if (result is null) return;

        if (result.Index < 0 || result.Index >= models.Length) {
            throw new InvalidOperationException("The selected model no longer exists");
        }

        var selection = models[result.Index];
        string? variant = null;
        if (selection.Model.Variants.Count > 0) {
            if (_state.Variant is { } currentVariant
                && selection.Model.Variants.Contains(currentVariant, StringComparer.OrdinalIgnoreCase)) {
                variant = currentVariant;
            } else {
                variant = await SelectVariantForModelAsync(selection.Model, cancellationToken);
                if (variant is null) return;
            }
        }

        var key = _auth.Get(selection.Provider.Id);
        if (key is null) {
            _view.WriteError($"No API key for {selection.Provider.Id}. Run /connect first.");
            return;
        }

        ResponsesAgent? newAgent = null;
        var previousProvider = _state.Provider;
        var previousModel = _state.Model;
        var previousVariant = _state.Variant;
        try {
            newAgent = CreateAgent(key, selection.Model, variant);

            _state.Provider = selection.Provider.Id;
            _state.Model = selection.Model.Id;
            _state.Variant = variant;
            _state.Save();

            ActivateAgent(newAgent);
            _view.WriteInfo($"Changed to: {_agent!.DisplayName}");
        } catch (Exception ex) {
            _state.Provider = previousProvider;
            _state.Model = previousModel;
            _state.Variant = previousVariant;
            newAgent?.Dispose();
            _view.WriteError($"Model change failed: {ex.Message}");
        }
    }

    private async Task ChangeVariantAsync(CancellationToken cancellationToken) {
        if (HasStreamingThreads()) {
            _view.WriteError("Stop active sessions before changing the variant.");
            return;
        }

        ResponsesAgent? currentAgent;
        lock (_gate) {
            currentAgent = _agent;
        }

        if (currentAgent is null) {
            _view.WriteError("Run /connect first.");
            return;
        }

        var (provider, model) = RequireCurrentSelection();
        var key = _auth.Get(provider.Id);
        if (key is null) {
            _view.WriteError("No API key. Run /connect first.");
            return;
        }

        if (model.Variants.Count == 0) {
            _view.WriteError($"No reasoning effort options available for {model.Id}.");
            return;
        }

        var value = await SelectVariantForModelAsync(model, cancellationToken);
        if (value is null) return;

        ResponsesAgent? newAgent = null;
        var previousVariant = _state.Variant;
        try {
            newAgent = CreateAgent(key, model, value);
            _state.Variant = value;
            _state.Save();

            ActivateAgent(newAgent);
            _view.WriteInfo($"Changed to: {_agent!.DisplayName}");
        } catch (Exception ex) {
            _state.Variant = previousVariant;
            newAgent?.Dispose();
            _view.WriteError($"Change failed: {ex.Message}");
        }
    }

    private void ActivateAgent(ResponsesAgent newAgent) {
        ResponsesAgent? oldAgent;
        lock (_gate) {
            oldAgent = _agent;
            _agent = newAgent;
            _view.SetModelName(newAgent.DisplayName);
        }

        DisposePreviousAgent(oldAgent);
    }

    private (ProviderPreset Provider, ModelPreset Model) RequireCurrentSelection() {
        _state.Validate();
        if (_state.Provider is null || _state.Model is null) {
            throw new InvalidOperationException("State has no current model");
        }

        var provider = _catalog.FindProvider(_state.Provider)
                       ?? throw new InvalidOperationException($"Unknown provider '{_state.Provider}' in state.json");
        var model = _catalog.FindModel(provider.Id, _state.Model)
                    ?? throw new InvalidOperationException(
                        $"Unknown model '{_state.Model}' for provider '{provider.Id}' in state.json");
        return (provider, model);
    }

    private bool HasStreamingThreads() {
        lock (_gate) {
            return _threads.Values.Any(thread => thread.IsStreaming);
        }
    }

    private void RefreshSessionCost(SessionThread thread) {
        var currency = _catalog.FindModel(_state.Provider, _state.Model)?.Cost?.Currency;
        if (thread.Session.Cost > 0 && currency is not null) {
            _view.SetSessionCost($"{currency}{thread.Session.Cost:0.00}");
        } else {
            _view.SetSessionCost(string.Empty);
        }
    }

    private void DisposePreviousAgent(ResponsesAgent? agent) {
        try {
            agent?.Dispose();
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
        _agent?.Dispose();
    }

    private ResponsesAgent CreateAgent(string key, ModelPreset model, string? variant) {
        return ResponsesAgent.Create(key, model, variant, Instruction.Build(model.Instructions, _store.Workspace));
    }

    private static string ErrorMessage(Exception exception) => string.IsNullOrWhiteSpace(exception.Message)
        ? exception.GetType().Name
        : exception.Message;

    private static string ReadCommand(string arguments) {
        try {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("command", out var command)
                && command.ValueKind == JsonValueKind.String) {
                return command.GetString() ?? throw new InvalidOperationException("Tool command is null");
            }
        } catch (JsonException ex) {
            throw new InvalidOperationException("Tool arguments are invalid JSON", ex);
        }

        throw new InvalidOperationException("Tool arguments do not contain a string command");
    }
}
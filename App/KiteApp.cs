using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Kite.Agent;
using AgentBase = Kite.Agent.Agent;
using Kite.Commands;
using Kite.Configuration;
using Kite.Context;
using Kite.Sessions;
using Kite.Tools;
using Kite.Ui;

namespace Kite.App;

public sealed class KiteApp : IDisposable {
    private const double AutoCompactionThreshold = 0.8;
    private readonly ModelCatalog _catalog;
    private readonly KiteAuth _auth;
    private readonly KiteState _state;
    private readonly FullScreenChatView _view;
    private readonly SessionStore _store;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SessionThread> _threads = [];
    private AgentBase? _agent;
    private SessionThread _activeThread;
    private bool _stopping;
    private bool _disposed;

    public KiteApp(AgentBase? agent, FullScreenChatView view, ModelCatalog catalog, KiteAuth auth, KiteState state,
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
            _view.SkillProvider = () => Skills.List(_store.Workspace);
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

                if (input.StartsWith('$')) {
                    HandleSkill(input, cancellationToken);
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

    private void StartTurn(string input, CancellationToken cancellationToken, string? displayText = null) {
        lock (_gate) {
            if (_agent is null) {
                _view.WriteError("No API key. Run /connect first.");
                return;
            }

            if (_activeThread.IsStreaming) {
                _view.WriteError("This session is still generating. Use /sessions to switch.");
                return;
            }

            var display = displayText ?? input;
            var thread = _activeThread;
            var agent = _agent;
            var turnSnapshot = new TurnSnapshot(display, thread.Session.Messages.Count, thread.EntryCount,
                thread.Session.LastPromptTokens);

            _store.Append(thread.Session, [ConversationMessage.User(input)]);
            thread.AddUserMessage(display);
            thread.StartTurn();
            _view.AddUserMessage(display);
            _view.StartAssistantTurn();

            var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            thread.TurnCancellation = turnCancellation;
            thread.TurnTask = RunTurnAsync(thread, agent, turnSnapshot, turnCancellation);
        }
    }

    private void HandleSkill(string input, CancellationToken cancellationToken) {
        var firstSpace = input.IndexOf(' ');
        var name = firstSpace < 0 ? input[1..] : input[1..firstSpace];
        var extra = firstSpace < 0 ? string.Empty : input[(firstSpace + 1)..].Trim();

        var skill = Skills.Find(_store.Workspace, name);
        if (skill is null) {
            var available = Skills.List(_store.Workspace);
            var hint = available.Count == 0
                ? "No skills found in ./.agents/skills/ or ~/.kite/skills/"
                : $"Available skills: {string.Join(", ", available.Select(s => "$" + s.Name))}";
            _view.WriteError($"Skill '${name}' not found. {hint}");
            return;
        }

        var prompt = extra.Length == 0 ? skill.Content : $"{skill.Content}\n\n{extra}";
        StartTurn(prompt, cancellationToken, displayText: input);
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

    private async Task RunTurnAsync(SessionThread thread, AgentBase agent, TurnSnapshot turnSnapshot,
        CancellationTokenSource turnCancellation) {
        var reply = AgentReply.Empty;
        Exception? failure = null;
        Exception? saveException = null;
        var rethrowSaveException = false;
        bool shouldAutoCompact;

        try {
            IReadOnlyList<ConversationMessage> conversation;
            lock (_gate) {
                conversation = [.. thread.Messages];
            }

            reply = await agent.StreamReplyAsync(conversation, thread.Session.Id,
                agentEvent => HandleAgentEventAsync(thread, agentEvent),
                (calls, cancellationToken) => ExecuteToolCallsAsync(thread, turnSnapshot, calls, cancellationToken),
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
                    thread.PushUndo(turnSnapshot);
                    var cachedTokens = Math.Clamp(reply.CachedTokens, 0, reply.PromptTokens);
                    thread.Session.PromptTokens += reply.PromptTokens;
                    thread.Session.CompletionTokens += reply.CompletionTokens;
                    thread.Session.CachedTokens += cachedTokens;
                    thread.Session.LastPromptTokens = reply.ContextTokens;

                    var contextLimit = _catalog.FindModel(_state.Provider, _state.Model)?.Limit.Context ?? 32_768;
                    shouldAutoCompact = failure is null && !interrupted && contextLimit > 0 &&
                                        reply.ContextTokens >= contextLimit * AutoCompactionThreshold;

                    var model = _catalog.FindModel(_state.Provider, _state.Model);
                    if (model?.Cost?.CurrentPrice() is {
                            Input: { } input, Output: { } output,
                            CacheRead: { } cacheRead
                        }) {
                        var uncached = Math.Max(0, reply.PromptTokens - cachedTokens);
                        thread.Session.Cost +=
                            (decimal)(uncached * input + cachedTokens * cacheRead + reply.CompletionTokens * output) /
                            1_000_000m;
                        _store.SaveCost(thread.Session);
                        if (!_stopping && ReferenceEquals(_activeThread, thread)) {
                            RefreshSessionCost(thread);
                        }
                    } else {
                        _store.SaveCost(thread.Session);
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

        if (shouldAutoCompact) {
            await CompactAsync(string.Empty, CancellationToken.None, thread);
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
        TurnSnapshot snapshot,
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
            .Select(call => Task.Run(() => ExecuteToolCallAsync(thread, snapshot, call, cancellationToken),
                cancellationToken))
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

    private static async Task<string> ExecuteToolCallAsync(
        SessionThread thread,
        TurnSnapshot snapshot,
        ToolCall call,
        CancellationToken cancellationToken) {
        try {
            if (call.Name.Equals(RunShell.DefaultName, StringComparison.Ordinal)) {
                return await RunShell.RunAsync(ReadCommand(call.Arguments), thread.Session.Workspace,
                    cancellationToken);
            }

            return call.Name.Equals(SkillTool.DefaultName, StringComparison.Ordinal)
                ? SkillTool.Execute(call, thread.Session.Workspace)
                : FileTools.Execute(call, thread.Session.Workspace, snapshot, cancellationToken);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            return $"error: {ErrorMessage(ex)}";
        }
    }

    private async Task HandleSlashAsync(string input, CancellationToken cancellationToken) {
        var spaceIndex = input.IndexOf(' ');
        var arg = spaceIndex >= 0 ? input[(spaceIndex + 1)..].Trim() : string.Empty;

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
            case "/undo":
                await UndoAsync();
                break;
            case "/compact":
                await CompactAsync(arg, cancellationToken);
                break;
            case "/new":
                NewSession();
                break;
            case "/sessions":
                await SwitchSessionAsync(cancellationToken);
                break;
            case "/stats":
                await ShowStatsAsync(cancellationToken);
                break;
            default:
                _view.WriteError("Unknown command.");
                break;
        }
    }

    private async Task CompactAsync(string focus, CancellationToken cancellationToken,
        SessionThread? targetThread = null) {
        if (_agent is null) {
            _view.WriteError("No API key. Run /connect first.");
            return;
        }

        var thread = targetThread ?? _activeThread;
        lock (_gate) {
            if (_stopping) return;

            if (thread.IsStreaming) {
                _view.WriteError("Cannot compact while generation is running. Press Esc to stop first.");
                return;
            }

            if (thread.Session.Messages.Count == 0) {
                _view.WriteError("Nothing to compact in an empty session.");
                return;
            }

            if (CompactionService.IsAlreadyCompacted(thread.Session.Messages)) {
                _view.WriteError("Session is already compacted with no new messages.");
                return;
            }
        }

        var agent = _agent;
        var beforeCount = thread.Session.Messages.Count;
        var prompt = CompactionService.BuildPrompt(thread.Session.Messages, focus);

        var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        int entryCountBefore;
        lock (_gate) {
            entryCountBefore = thread.EntryCount;
            thread.TurnCancellation = turnCancellation;
            thread.StartTurn();
            _view.StartAssistantTurn();
        }

        var reply = AgentReply.Empty;
        var summaryText = new StringBuilder();
        Exception? failure = null;

        try {
            reply = await agent.StreamReplyAsync(
                [ConversationMessage.User(prompt)],
                thread.Session.Id,
                agentEvent => {
                    lock (_gate) {
                        if (agentEvent.Kind == AgentEventKind.TextDelta) {
                            summaryText.Append(agentEvent.Text);
                            thread.AppendAssistant(agentEvent.Text);
                            if (!_stopping && ReferenceEquals(_activeThread, thread)) {
                                _view.AppendAssistantChunk(agentEvent.Text);
                            }
                        } else if (agentEvent.Kind == AgentEventKind.ReasoningDelta) {
                            thread.AppendReasoning(agentEvent.Text);
                            if (!_stopping && ReferenceEquals(_activeThread, thread)) {
                                _view.AppendReasoningChunk(agentEvent.Text);
                            }
                        }
                    }

                    return Task.CompletedTask;
                },
                null,
                turnCancellation.Token);
        } catch (OperationCanceledException) when (turnCancellation.IsCancellationRequested) { } catch (Exception ex) {
            failure = ex;
        } finally {
            lock (_gate) {
                thread.CompleteTurn();
                if (ReferenceEquals(thread.TurnCancellation, turnCancellation)) {
                    thread.TurnCancellation = null;
                }
            }

            turnCancellation.Dispose();
        }

        if (cancellationToken.IsCancellationRequested || turnCancellation.IsCancellationRequested) {
            lock (_gate) {
                thread.TruncateEntries(entryCountBefore);
                if (_stopping || !ReferenceEquals(_activeThread, thread)) return;

                _view.EndAssistantTurn();
                _view.LoadTranscript(thread.Snapshot(), streaming: false);
                _view.WriteInfo("Compaction cancelled.");
            }

            return;
        }

        if (failure is not null) {
            lock (_gate) {
                thread.TruncateEntries(entryCountBefore);
                if (_stopping || !ReferenceEquals(_activeThread, thread)) return;

                _view.EndAssistantTurn();
                _view.LoadTranscript(thread.Snapshot(), streaming: false);
                _view.WriteError($"Compaction failed: {ErrorMessage(failure)}");
            }

            return;
        }

        var cleanedSummary = CompactionService.CleanSummaryText(summaryText.ToString());
        if (string.IsNullOrWhiteSpace(cleanedSummary)) {
            lock (_gate) {
                thread.TruncateEntries(entryCountBefore);
                if (_stopping || !ReferenceEquals(_activeThread, thread)) return;

                _view.EndAssistantTurn();
                _view.LoadTranscript(thread.Snapshot(), streaming: false);
                _view.WriteError("Compaction produced an empty summary. Session unchanged.");
            }

            return;
        }

        lock (_gate) {
            var cachedTokens = Math.Clamp(reply.CachedTokens, 0, reply.PromptTokens);
            thread.Session.PromptTokens += reply.PromptTokens;
            thread.Session.CompletionTokens += reply.CompletionTokens;
            thread.Session.CachedTokens += cachedTokens;
            thread.Session.LastPromptTokens = 0;

            var model = _catalog.FindModel(_state.Provider, _state.Model);
            if (model?.Cost?.CurrentPrice() is {
                    Input: { } inputPrice, Output: { } outputPrice,
                    CacheRead: { } cacheReadPrice
                }) {
                var uncached = Math.Max(0, reply.PromptTokens - cachedTokens);
                thread.Session.Cost +=
                    (decimal)(uncached * inputPrice + cachedTokens * cacheReadPrice +
                              reply.CompletionTokens * outputPrice) /
                    1_000_000m;
                _store.SaveCost(thread.Session);
            } else {
                _store.SaveCost(thread.Session);
            }

            var newMessages = CompactionService.CreateCompactedMessages(cleanedSummary);
            _store.RewriteMessages(thread.Session, newMessages);
            thread.ResetWithCompaction(cleanedSummary);

            if (!_stopping && ReferenceEquals(_activeThread, thread)) {
                _view.EndAssistantTurn();
                _view.LoadTranscript(thread.Snapshot(), streaming: false);
                RefreshSessionCost(thread);
                _view.WriteInfo($"Compacted session ({beforeCount} messages summarized).");
            }
        }
    }

    private async Task UndoAsync() {
        Task? turnTask;
        lock (_gate) {
            CancelActiveTurn();
            turnTask = _activeThread.TurnTask;
        }

        if (turnTask is not null) {
            try {
                await turnTask;
            } catch {
                // ignored
            }
        }

        lock (_gate) {
            if (_activeThread.PopUndo() is { } snapshot) {
                var restored = snapshot.Restore();
                _activeThread.Session.LastPromptTokens = snapshot.LastPromptTokens;
                _store.Truncate(_activeThread.Session, snapshot.MessageIndex);
                _activeThread.TruncateEntries(snapshot.EntryIndex);

                _view.LoadTranscript(_activeThread.Snapshot(), streaming: false);
                RefreshSessionCost(_activeThread);
                _view.SetInputText(snapshot.Prompt);
                _view.WriteInfo(restored > 0
                    ? $"Undid last turn ({restored} {(restored == 1 ? "file" : "files")} restored)."
                    : "Undid last turn.");
                return;
            }

            var lastUserIndex = _activeThread.Session.Messages.FindLastIndex(m =>
                m.Role == "user" && !CompactionService.IsCompactionSummary(m.Content));
            if (lastUserIndex < 0) {
                _view.WriteError("Nothing to undo in this session.");
                return;
            }

            var prompt = _activeThread.Session.Messages[lastUserIndex].Content;
            _activeThread.Session.LastPromptTokens = 0;
            _store.Truncate(_activeThread.Session, lastUserIndex);
            _activeThread.ReloadFromSession();

            _view.LoadTranscript(_activeThread.Snapshot(), streaming: false);
            RefreshSessionCost(_activeThread);
            _view.SetInputText(prompt);
            _view.WriteInfo("Undid last turn.");
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

    private async Task ShowStatsAsync(CancellationToken cancellationToken) {
        IReadOnlyList<string> items;
        lock (_gate) {
            var turns = _activeThread.Session.Messages.Count(m => m.Role == "user");
            var steps = _activeThread.Session.Messages.Count;
            var turnsText = turns == 1 ? "1 turn" : $"{turns} turns";
            var stepsText = steps == 1 ? "1 step" : $"{steps} steps";
            var model = _catalog.FindModel(_state.Provider, _state.Model);
            var contextLimit = model?.Limit.Context ?? 0;
            var lastPrompt = _activeThread.Session.LastPromptTokens;

            var contextStr = lastPrompt switch {
                > 0 when contextLimit > 0 =>
                    $"{FormatTokens(lastPrompt)} / {FormatTokens(contextLimit)} ({(double)lastPrompt / contextLimit * 100.0:F1}%)",
                > 0 => FormatTokens(lastPrompt),
                _ => "-"
            };

            var tokensStr = _activeThread.Session.PromptTokens > 0
                ? $"↑{FormatTokens(_activeThread.Session.PromptTokens)} ({(double)_activeThread.Session.CachedTokens / _activeThread.Session.PromptTokens * 100.0:F1}% cached)  ↓{FormatTokens(_activeThread.Session.CompletionTokens)}"
                : "-";

            var costStr = model?.Cost is { Currency: var currency }
                ? $"{currency}{_activeThread.Session.Cost:0.00}"
                : "-";

            items = [
                $"Session\t{turnsText} ({stepsText})",
                $"Context\t{contextStr}",
                $"Tokens\t{tokensStr}",
                $"Cost\t{costStr}"
            ];
        }

        await _view.ReadChoiceAsync(items, cancellationToken);
    }

    private static string FormatTokens(int count) => count switch {
        >= 1_000_000 => $"{count / 1_000_000.0:0.#}M",
        >= 1_000 => $"{count / 1_000.0:0.#}k",
        _ => count.ToString()
    };

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
            provider.Id.Equals(_state.Provider, StringComparison.OrdinalIgnoreCase)) {
            var model = _state.Model is null
                ? null
                : provider.Models.FirstOrDefault(m =>
                    m.Id.Equals(_state.Model, StringComparison.OrdinalIgnoreCase));
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

                AgentBase? newAgent = null;
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

        AgentBase? agent = null;
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
                var isCurrent = p.Id.Equals(_state.Provider, StringComparison.OrdinalIgnoreCase);
                return $"{(isCurrent ? "* " : "  ")}{p.Id}";
            })
            .ToArray();
        var index = await SelectIndexAsync(choices, _catalog.Providers.Count, cancellationToken);
        return index is null ? null : _catalog.Providers[index.Value];
    }

    private async Task<ModelPreset?> SelectModelForProviderAsync(
        ProviderPreset provider,
        CancellationToken cancellationToken) {
        var choices = provider.Models
            .Select(model => {
                var selected = provider.Id.Equals(_state.Provider, StringComparison.OrdinalIgnoreCase)
                               && model.Id.Equals(_state.Model, StringComparison.OrdinalIgnoreCase);
                return $"{(selected ? "* " : "  ")}{model.Id}";
            })
            .ToArray();
        var index = await SelectIndexAsync(choices, provider.Models.Count, cancellationToken);
        return index is null ? null : provider.Models[index.Value];
    }

    private async Task<string?> SelectVariantForModelAsync(
        ModelPreset model,
        CancellationToken cancellationToken) {
        if (model.Variants.Count == 0) return null;

        var choices = model.Variants
            .Select(variant => {
                var selected = _state.Variant is not null && variant.Equals(_state.Variant, StringComparison.Ordinal);
                return $"{(selected ? "* " : "  ")}{variant}";
            })
            .ToArray();
        var index = await SelectIndexAsync(choices, model.Variants.Count, cancellationToken);
        return index is null ? null : model.Variants[index.Value];
    }

    private async Task<int?> SelectIndexAsync(
        IReadOnlyList<string> choices,
        int count,
        CancellationToken cancellationToken) {
        var result = await _view.ReadChoiceAsync(choices, cancellationToken);
        if (result is null) return null;

        if (result.Index < 0 || result.Index >= count) {
            throw new InvalidOperationException("The selected option no longer exists");
        }

        return result.Index;
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
                var selected = selection.Provider.Id.Equals(_state.Provider, StringComparison.OrdinalIgnoreCase)
                               && selection.Model.Id.Equals(_state.Model, StringComparison.OrdinalIgnoreCase);
                return $"{(selected ? "* " : "  ")}{selection.Provider.Id}/{selection.Model.Id}";
            })
            .ToArray();
        if (await SelectIndexAsync(choices, models.Length, cancellationToken) is not { } index) return;

        var selection = models[index];
        string? variant = null;
        if (selection.Model.Variants.Count > 0) {
            if (_state.Variant is { } currentVariant
                && selection.Model.Variants.Contains(currentVariant, StringComparer.Ordinal)) {
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

        AgentBase? newAgent = null;
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

        AgentBase? currentAgent;
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

        AgentBase? newAgent = null;
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

    private void ActivateAgent(AgentBase newAgent) {
        AgentBase? oldAgent;
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

    private void DisposePreviousAgent(AgentBase? agent) {
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

    private AgentBase CreateAgent(string key, ModelPreset model, string? variant) {
        return AgentFactory.Create(key, model, variant, _store.Workspace);
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
using System.Runtime.ExceptionServices;
using System.Text;
using Kite.Agent;
using Kite.Config;
using Kite.Context;
using Kite.Sessions;
using Kite.Tools;
using Kite.Ui;

namespace Kite.App;

internal sealed class SessionManager {
    private const double AutoCompactionThreshold = 0.8;
    private readonly FullScreenChatView _view;
    private readonly SessionStore _store;
    private readonly IReadOnlyList<Skill> _skills;
    private readonly ModelCatalog _catalog;
    private readonly KiteState _state;
    private readonly ModelConnection _models;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SessionTranscript> _transcripts = [];
    private SessionTranscript _activeTranscript;
    private bool _stopping;

    public SessionManager(FullScreenChatView view, SessionStore store, IReadOnlyList<Skill> skills,
        ModelCatalog catalog, KiteState state, ModelConnection models) {
        _view = view;
        _store = store;
        _skills = skills;
        _catalog = catalog;
        _state = state;
        _models = models;

        var sessions = _store.List();
        if (sessions.Count == 0) {
            sessions = [_store.Create()];
        }

        foreach (var session in sessions) {
            AddTranscript(SessionTranscript.Open(session));
        }

        _activeTranscript = _transcripts[sessions[0].Id];
    }

    public bool HasStreaming {
        get {
            lock (_gate) {
                return _transcripts.Values.Any(transcript => transcript.IsStreaming);
            }
        }
    }

    public void ShowInitialState(bool disconnected) {
        lock (_gate) {
            _view.LoadTranscript(_activeTranscript.Snapshot(), _activeTranscript.IsStreaming);
            RefreshSessionCost(_activeTranscript);
            if (disconnected) {
                _view.WriteInfo("Not connected. Run /connect to add a key; use /model to change the model.");
            }
        }
    }

    private SessionTranscript AddTranscript(SessionTranscript transcript) {
        _transcripts.Add(transcript.Session.Id, transcript);
        return transcript;
    }

    public void StartTurn(string input, CancellationToken ct, string? displayText = null) {
        lock (_gate) {
            var agent = _models.Agent;
            if (agent is null) {
                _view.WriteError("No API key. Run /connect first.");
                return;
            }

            if (_activeTranscript.IsStreaming) {
                _view.WriteError("This session is still generating. Use /sessions to switch.");
                return;
            }

            var display = displayText ?? input;
            var transcript = _activeTranscript;
            var turnSnapshot = new TurnSnapshot(display, transcript.Session.Messages.Count, transcript.EntryCount,
                transcript.Session.LastPromptTokens);

            _store.Append(transcript.Session, [ConversationMessage.User(input)]);
            transcript.AddUserMessage(display);
            transcript.StartTurn();
            _view.AddUserMessage(display);
            _view.StartAssistantTurn();

            var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            transcript.TurnCancellation = turnCancellation;
            transcript.TurnTask = RunTurnAsync(transcript, agent, turnSnapshot, turnCancellation);
        }
    }

    public void StartSkill(string input, CancellationToken ct) {
        var firstSpace = input.IndexOf(' ');
        var name = firstSpace < 0 ? input[1..] : input[1..firstSpace];
        var extra = firstSpace < 0 ? string.Empty : input[(firstSpace + 1)..].Trim();

        var skill = _skills.FirstOrDefault(candidate =>
            candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (skill is null) {
            var hint = _skills.Count == 0
                ? "No skills found in ./.agents/skills/ or ~/.kite/skills/"
                : $"Available skills: {string.Join(", ", _skills.Select(s => "$" + s.Name))}";
            _view.WriteError($"Skill '${name}' not found. {hint}");
            return;
        }

        var prompt = extra.Length == 0 ? skill.Content : $"{skill.Content}\n\n{extra}";
        StartTurn(prompt, ct, displayText: input);
    }

    public bool CancelActiveTurn() {
        lock (_gate) {
            var cancellation = _activeTranscript.TurnCancellation;
            if (!_activeTranscript.IsStreaming || cancellation is null) {
                return false;
            }

            cancellation.Cancel();
            return true;
        }
    }

    private async Task RunTurnAsync(SessionTranscript transcript, AgentClient agent, TurnSnapshot turnSnapshot,
        CancellationTokenSource turnCancellation) {
        var reply = AgentReply.Empty;
        Exception? failure = null;
        Exception? saveException = null;
        var rethrowSaveException = false;
        bool shouldAutoCompact;

        try {
            IReadOnlyList<ConversationMessage> conversation;
            lock (_gate) {
                conversation = [.. transcript.Session.Messages];
            }

            reply = await agent.StreamReplyAsync(conversation, transcript.Session.Id,
                agentEvent => HandleAgentEventAsync(transcript, agentEvent),
                (calls, cancellationToken) => ExecuteToolCallsAsync(transcript, turnSnapshot, calls, cancellationToken),
                turnCancellation.Token);
        } catch (OperationCanceledException) when (turnCancellation.IsCancellationRequested) { } catch (Exception ex) {
            failure = ex;
        } finally {
            try {
                lock (_gate) {
                    var interrupted = turnCancellation.IsCancellationRequested;
                    string? saveFailure = null;
                    try {
                        var assistantText = transcript.CurrentAssistantText;
                        if (assistantText.Length > 0) {
                            _store.Append(transcript.Session, [ConversationMessage.Assistant(assistantText)]);
                        }
                    } catch (Exception ex) {
                        saveFailure = $"Could not save session: {ErrorMessage(ex)}";
                        transcript.AddError(saveFailure);
                        saveException = ex;
                        rethrowSaveException = _stopping;
                    }

                    var duration = transcript.CompleteTurn();
                    transcript.PushUndo(turnSnapshot);
                    RecordUsage(transcript, reply, reply.ContextTokens, true);

                    var contextLimit = _catalog.FindModel(_state.Provider, _state.Model)?.Limit.Context ?? 32_768;
                    shouldAutoCompact = failure is null && !interrupted && contextLimit > 0 &&
                                        reply.ContextTokens >= contextLimit * AutoCompactionThreshold;

                    var status =
                        $"{(interrupted ? "interrupted — " : "")}{duration.TotalSeconds:F1}s (↑{reply.PromptTokens} ↓{reply.CompletionTokens}{(interrupted ? " ⏹" : "")})";
                    transcript.AddInfo(status);
                    if (failure is not null) {
                        transcript.AddError(ErrorMessage(failure));
                    }

                    if (!_stopping && ReferenceEquals(_activeTranscript, transcript)) {
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
                    if (ReferenceEquals(transcript.TurnCancellation, turnCancellation)) {
                        transcript.TurnCancellation = null;
                    }
                }

                turnCancellation.Dispose();
            }
        }

        if (rethrowSaveException && saveException is not null) {
            ExceptionDispatchInfo.Capture(saveException).Throw();
        }

        if (shouldAutoCompact) {
            await CompactAsync(string.Empty, CancellationToken.None, transcript);
        }
    }

    private Task HandleAgentEventAsync(SessionTranscript transcript, AgentEvent agentEvent,
        StringBuilder? capturedText = null) {
        lock (_gate) {
            switch (agentEvent.Kind) {
                case AgentEventKind.TextDelta:
                    capturedText?.Append(agentEvent.Text);
                    transcript.AppendAssistant(agentEvent.Text);
                    if (!_stopping && ReferenceEquals(_activeTranscript, transcript)) {
                        _view.AppendAssistantChunk(agentEvent.Text);
                    }

                    break;
                case AgentEventKind.ReasoningDelta:
                    transcript.AppendReasoning(agentEvent.Text);
                    if (!_stopping && ReferenceEquals(_activeTranscript, transcript)) {
                        _view.AppendReasoningChunk(agentEvent.Text);
                    }

                    break;
                default:
                    throw new InvalidOperationException($"Unhandled agent event: {agentEvent.Kind}");
            }
        }

        return Task.CompletedTask;
    }

    private async Task<IReadOnlyList<string>> ExecuteToolCallsAsync(SessionTranscript transcript, TurnSnapshot snapshot,
        IReadOnlyList<ToolCall> calls, CancellationToken ct) {
        lock (_gate) {
            var assistantText = transcript.CurrentAssistantText;
            if (assistantText.Length > 0) {
                _store.Append(transcript.Session, [ConversationMessage.Assistant(assistantText)]);
            }

            foreach (var call in calls) {
                transcript.AddTool(call.Preview);
                if (!_stopping && ReferenceEquals(_activeTranscript, transcript)) {
                    _view.AppendToolLine(call.Preview);
                }
            }
        }

        // File tools are synchronous; offload each call so independent tools overlap.
        var tasks = calls
            .Select(call => Task.Run(() => ExecuteToolCallAsync(transcript, snapshot, _skills, call, ct),
                ct))
            .ToArray();
        var outputs = await Task.WhenAll(tasks);

        lock (_gate) {
            var messages = new List<ConversationMessage>(calls.Count * 2);
            messages.AddRange(calls.Select(ConversationMessage.FunctionCall));
            messages.AddRange(calls.Select((call, index) =>
                ConversationMessage.FunctionCallOutput(call.Id, outputs[index])));
            _store.Append(transcript.Session, messages);
        }

        return outputs;
    }

    private static async Task<string> ExecuteToolCallAsync(SessionTranscript transcript, TurnSnapshot snapshot,
        IReadOnlyList<Skill> skills, ToolCall call, CancellationToken ct) {
        try {
            return call.Name switch {
                RunShell.DefaultName => await RunShell.ExecuteAsync(
                    call, transcript.Session.Workspace, ct),
                SkillTool.DefaultName => SkillTool.Execute(call, skills),
                _ => FileTools.Execute(call, transcript.Session.Workspace, snapshot, ct)
            };
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            return $"error: {ErrorMessage(ex)}";
        }
    }

    public async Task CompactAsync(string focus, CancellationToken ct, SessionTranscript? targetTranscript = null) {
        var agent = _models.Agent;
        if (agent is null) {
            _view.WriteError("No API key. Run /connect first.");
            return;
        }

        var transcript = targetTranscript ?? _activeTranscript;
        CompactionService.CompactionSplit split;
        lock (_gate) {
            if (_stopping) return;

            if (transcript.IsStreaming) {
                _view.WriteError("Cannot compact while generation is running. Press Esc to stop first.");
                return;
            }

            if (transcript.Session.Messages.Count == 0) {
                _view.WriteError("Nothing to compact in an empty session.");
                return;
            }

            var candidate = CompactionService.TrySplit(transcript.Session.Messages);
            if (candidate is null) {
                _view.WriteError(
                    $"Nothing to compact: the last {CompactionService.KeepRecentTurns} turns are kept verbatim.");
                return;
            }

            split = candidate;
        }

        var conversation = new List<ConversationMessage>(split.Older.Count + 1);
        conversation.AddRange(split.Older);
        conversation.Add(ConversationMessage.User(CompactionService.BuildInstruction(split.PreviousSummary, focus)));

        var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int entryCountBefore;
        lock (_gate) {
            entryCountBefore = transcript.EntryCount;
            transcript.AddInfo(CompactionService.DividerText);
            transcript.TurnCancellation = turnCancellation;
            transcript.StartTurn();
            if (ReferenceEquals(_activeTranscript, transcript)) {
                _view.StartAssistantTurn();
            }
        }

        var reply = AgentReply.Empty;
        var summaryText = new StringBuilder();
        Exception? failure = null;

        try {
            reply = await agent.StreamReplyAsync(
                conversation,
                transcript.Session.Id,
                agentEvent => HandleAgentEventAsync(transcript, agentEvent, summaryText),
                null,
                turnCancellation.Token);
        } catch (OperationCanceledException) when (turnCancellation.IsCancellationRequested) { } catch (Exception ex) {
            failure = ex;
        } finally {
            lock (_gate) {
                transcript.CompleteTurn();
                if (ReferenceEquals(transcript.TurnCancellation, turnCancellation)) {
                    transcript.TurnCancellation = null;
                }
            }

            turnCancellation.Dispose();
        }

        if (ct.IsCancellationRequested || turnCancellation.IsCancellationRequested) {
            AbortCompaction(transcript, entryCountBefore, "Compaction cancelled.", error: false);
            return;
        }

        if (failure is not null) {
            AbortCompaction(transcript, entryCountBefore, $"Compaction failed: {ErrorMessage(failure)}", error: true);
            return;
        }

        var cleanedSummary = CompactionService.CleanSummaryText(summaryText.ToString());
        if (string.IsNullOrWhiteSpace(cleanedSummary)) {
            AbortCompaction(transcript, entryCountBefore, "Compaction produced an empty summary. Session unchanged.",
                error: true);
            return;
        }

        lock (_gate) {
            RecordUsage(transcript, reply, lastPromptTokens: 0, refreshCost: false);

            // ponytail: replacing the file reclaims the shadowed messages now and keeps Load free of
            // folding; an appended checkpoint would preserve them and defer both.
            _store.RewriteMessages(transcript.Session,
                CompactionService.CreateCompactedMessages(cleanedSummary, split.Retained));
            transcript.DiscardUndo();

            if (!_stopping && ReferenceEquals(_activeTranscript, transcript)) {
                _view.EndAssistantTurn();
                _view.LoadTranscript(transcript.Snapshot(), streaming: false);
                RefreshSessionCost(transcript);
            }
        }
    }

    private void RecordUsage(SessionTranscript transcript, AgentReply reply, int lastPromptTokens, bool refreshCost) {
        var cachedTokens = Math.Clamp(reply.CachedTokens, 0, reply.PromptTokens);
        transcript.Session.PromptTokens += reply.PromptTokens;
        transcript.Session.CompletionTokens += reply.CompletionTokens;
        transcript.Session.CachedTokens += cachedTokens;
        transcript.Session.LastPromptTokens = lastPromptTokens;

        if (_catalog.FindModel(_state.Provider, _state.Model)?.Cost?.CurrentPrice() is {
                Input: { } input, Output: { } output,
                CacheRead: { } cacheRead
            }) {
            var uncached = Math.Max(0, reply.PromptTokens - cachedTokens);
            transcript.Session.Cost +=
                (decimal)(uncached * input + cachedTokens * cacheRead + reply.CompletionTokens * output) /
                1_000_000m;
        }

        _store.SaveCost(transcript.Session);
        if (refreshCost && !_stopping && ReferenceEquals(_activeTranscript, transcript)) {
            RefreshSessionCost(transcript);
        }
    }

    private void AbortCompaction(SessionTranscript transcript, int entryCount, string message, bool error) {
        lock (_gate) {
            transcript.TruncateEntries(entryCount);
            if (_stopping || !ReferenceEquals(_activeTranscript, transcript)) return;

            _view.EndAssistantTurn();
            _view.LoadTranscript(transcript.Snapshot(), streaming: false);
            if (error) {
                _view.WriteError(message);
            } else {
                _view.WriteInfo(message);
            }
        }
    }

    public async Task UndoAsync() {
        Task? turnTask;
        lock (_gate) {
            CancelActiveTurn();
            turnTask = _activeTranscript.TurnTask;
        }

        if (turnTask is not null) {
            try {
                await turnTask;
            } catch {
                // ignored
            }
        }

        lock (_gate) {
            if (_activeTranscript.PopUndo() is { } snapshot) {
                var restored = snapshot.Restore();
                _activeTranscript.Session.LastPromptTokens = snapshot.LastPromptTokens;
                _store.Truncate(_activeTranscript.Session, snapshot.MessageIndex);
                _activeTranscript.TruncateEntries(snapshot.EntryIndex);

                _view.LoadTranscript(_activeTranscript.Snapshot(), streaming: false);
                RefreshSessionCost(_activeTranscript);
                _view.SetInputText(snapshot.Prompt);
                _view.WriteInfo(restored > 0
                    ? $"Undid last turn ({restored} {(restored == 1 ? "file" : "files")} restored)."
                    : "Undid last turn.");
                return;
            }

            var lastUserIndex = _activeTranscript.Session.Messages.FindLastIndex(m =>
                m.Role == "user" && !CompactionService.IsCompactionSummary(m.Content));
            if (lastUserIndex < 0) {
                _view.WriteError("Nothing to undo in this session.");
                return;
            }

            var prompt = _activeTranscript.Session.Messages[lastUserIndex].Content;
            _activeTranscript.Session.LastPromptTokens = 0;
            _store.Truncate(_activeTranscript.Session, lastUserIndex);
            _activeTranscript.ReloadFromSession();

            _view.LoadTranscript(_activeTranscript.Snapshot(), streaming: false);
            RefreshSessionCost(_activeTranscript);
            _view.SetInputText(prompt);
            _view.WriteInfo("Undid last turn.");
        }
    }

    public void NewSession() {
        lock (_gate) {
            _activeTranscript = AddTranscript(SessionTranscript.Open(_store.Create()));
            _view.LoadTranscript(_activeTranscript.Snapshot(), streaming: false);
            RefreshSessionCost(_activeTranscript);
        }
    }

    public async Task SwitchAsync(CancellationToken ct) {
        while (true) {
            SessionTranscript[] transcripts;
            string[] choices;
            lock (_gate) {
                transcripts = [.. _transcripts.Values.OrderByDescending(transcript => transcript.Session.UpdatedAt)];
                choices = [
                    .. transcripts
                        .Select(transcript => {
                            var label = SessionStore.Label(
                                transcript.Session,
                                ReferenceEquals(transcript, _activeTranscript));
                            return transcript.IsStreaming ? $"{label} · running" : label;
                        })
                ];
            }

            var result = await _view.ReadChoiceAsync(choices, ct, true);
            if (result is null) return;

            if (result.Index < 0 || result.Index >= transcripts.Length) {
                throw new InvalidOperationException("The selected session no longer exists");
            }

            if (result.DeleteRequested) {
                DeleteSession(transcripts[result.Index]);
                continue;
            }

            lock (_gate) {
                _activeTranscript = transcripts[result.Index];
                _view.LoadTranscript(_activeTranscript.Snapshot(), _activeTranscript.IsStreaming);
                RefreshSessionCost(_activeTranscript);
            }

            return;
        }
    }

    private void DeleteSession(SessionTranscript transcript) {
        lock (_gate) {
            if (transcript.IsStreaming) {
                _view.WriteError("Cannot delete a running session.");
                return;
            }

            _store.Delete(transcript.Session);
            if (!_transcripts.Remove(transcript.Session.Id)) {
                throw new InvalidOperationException("The selected session no longer exists");
            }

            if (!ReferenceEquals(transcript, _activeTranscript)) return;

            _activeTranscript = _transcripts.Values.OrderByDescending(candidate => candidate.Session.UpdatedAt)
                                    .FirstOrDefault()
                                ?? AddTranscript(SessionTranscript.Open(_store.Create()));
            _view.LoadTranscript(_activeTranscript.Snapshot(), _activeTranscript.IsStreaming);
        }
    }

    public async Task ShowStatsAsync(CancellationToken ct) {
        IReadOnlyList<string> items;
        lock (_gate) {
            var turns = _activeTranscript.Session.Messages.Count(m => m.Role == "user");
            var steps = _activeTranscript.Session.Messages.Count;
            var turnsText = turns == 1 ? "1 turn" : $"{turns} turns";
            var stepsText = steps == 1 ? "1 step" : $"{steps} steps";
            var model = _catalog.FindModel(_state.Provider, _state.Model);
            var contextLimit = model?.Limit.Context ?? 0;
            var lastPrompt = _activeTranscript.Session.LastPromptTokens;

            var contextStr = lastPrompt switch {
                > 0 when contextLimit > 0 =>
                    $"{FormatTokens(lastPrompt)} / {FormatTokens(contextLimit)} ({(double)lastPrompt / contextLimit * 100.0:F1}%)",
                > 0 => FormatTokens(lastPrompt),
                _ => "-"
            };

            var tokensStr = _activeTranscript.Session.PromptTokens > 0
                ? $"↑{FormatTokens(_activeTranscript.Session.PromptTokens)} ({(double)_activeTranscript.Session.CachedTokens / _activeTranscript.Session.PromptTokens * 100.0:F1}% cached)  ↓{FormatTokens(_activeTranscript.Session.CompletionTokens)}"
                : "-";

            var costStr = model?.Cost is { Currency: var currency }
                ? $"{currency}{_activeTranscript.Session.Cost:0.00}"
                : "-";

            items = [
                $"Session\t{turnsText} ({stepsText})",
                $"Context\t{contextStr}",
                $"Tokens\t{tokensStr}",
                $"Cost\t{costStr}"
            ];
        }

        await _view.ReadChoiceAsync(items, ct);
    }

    private static string FormatTokens(int count) => count switch {
        >= 1_000_000 => $"{count / 1_000_000.0:0.#}M",
        >= 1_000 => $"{count / 1_000.0:0.#}k",
        _ => count.ToString()
    };

    private void RefreshSessionCost(SessionTranscript transcript) {
        var currency = _catalog.FindModel(_state.Provider, _state.Model)?.Cost?.Currency;
        if (transcript.Session.Cost > 0 && currency is not null) {
            _view.SetSessionCost($"{currency}{transcript.Session.Cost:0.00}");
        } else {
            _view.SetSessionCost(string.Empty);
        }
    }

    public async Task StopAsync() {
        Task[] tasks;
        lock (_gate) {
            _stopping = true;
            foreach (var transcript in _transcripts.Values) {
                transcript.TurnCancellation?.Cancel();
            }

            tasks = [
                .. _transcripts.Values
                    .Select(transcript => transcript.TurnTask)
                    .OfType<Task>()
                    .Where(task => !task.IsCompleted)
            ];
        }

        if (tasks.Length > 0) {
            await Task.WhenAll(tasks);
        }
    }

    private static string ErrorMessage(Exception exception) => string.IsNullOrWhiteSpace(exception.Message)
        ? exception.GetType().Name
        : exception.Message;
}
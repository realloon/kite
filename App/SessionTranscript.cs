using System.Diagnostics;
using System.Text;
using Kite.Agent;
using Kite.Sessions;
using Kite.Ui;

namespace Kite.App;

internal sealed class SessionTranscript(Session session) {
    private readonly List<LiveEntry> _entries = [];
    private readonly Stack<TurnSnapshot> _undoStack = new();
    private LiveEntry? _assistant;
    private LiveEntry? _reasoning;

    public Session Session { get; } = session;
    public bool IsStreaming { get; private set; }
    public CancellationTokenSource? TurnCancellation { get; set; }
    public Task? TurnTask { get; set; }
    private Stopwatch? TurnElapsed { get; set; }

    public int EntryCount => _entries.Count;
    public string CurrentAssistantText => _assistant?.Text.ToString() ?? string.Empty;

    public static SessionTranscript Open(Session session) {
        var transcript = new SessionTranscript(session);
        transcript.PopulateEntries();
        return transcript;
    }

    public void ReloadFromSession() {
        _entries.Clear();
        _undoStack.Clear();
        _assistant = null;
        _reasoning = null;
        IsStreaming = false;
        TurnCancellation = null;
        TurnTask = null;
        PopulateEntries();
    }

    private void PopulateEntries() {
        foreach (var message in Session.Messages) {
            switch (message) {
                case FunctionCallOutputMessage:
                    continue;
                case FunctionCallMessage call:
                    AddEntry(TranscriptEntryKind.Tool, ToolCall.FormatPreview(call.Name, call.Arguments));
                    break;
                case UserMessage user when CompactionService.IsCompactionSummary(user.Content):
                    AddEntry(TranscriptEntryKind.Info, CompactionService.DividerText);
                    AddEntry(TranscriptEntryKind.Assistant, CompactionService.ExtractSummary(user.Content));
                    break;
                case UserMessage user:
                    AddEntry(TranscriptEntryKind.User, user.Content);
                    break;
                case AssistantMessage assistant:
                    AddEntry(TranscriptEntryKind.Assistant, assistant.Content);
                    break;
            }
        }
    }

    public void AddUserMessage(string text) => AddEntry(TranscriptEntryKind.User, text);

    public void StartTurn() {
        if (IsStreaming) {
            throw new InvalidOperationException("Session is already streaming");
        }

        IsStreaming = true;
        TurnElapsed = Stopwatch.StartNew();
        _assistant = new LiveEntry(TranscriptEntryKind.Assistant) { IsStreaming = true };
        _entries.Add(_assistant);
        _reasoning = null;
    }

    public void AppendAssistant(string text) => EnsureAssistant().Text.Append(text);

    public void AppendReasoning(string text) {
        var assistant = EnsureAssistant();
        if (_reasoning is null) {
            _reasoning = new LiveEntry(TranscriptEntryKind.Reasoning) { IsStreaming = true };
            _entries.Insert(_entries.IndexOf(assistant), _reasoning);
        }

        _reasoning.Text.Append(text);
    }

    public void AddTool(string line) {
        FinalizeActiveEntries();
        AddEntry(TranscriptEntryKind.Tool, line);
    }

    public TimeSpan CompleteTurn() {
        if (!IsStreaming || TurnElapsed is null) {
            throw new InvalidOperationException("Session has no streaming turn");
        }

        var elapsed = TurnElapsed.Elapsed;
        IsStreaming = false;
        FinalizeActiveEntries();
        TurnElapsed = null;
        return elapsed;
    }

    public void AddInfo(string text) => AddEntry(TranscriptEntryKind.Info, text);
    public void AddError(string text) => AddEntry(TranscriptEntryKind.Error, text);

    public void TruncateEntries(int targetCount) {
        if (targetCount < 0 || targetCount > _entries.Count) {
            throw new ArgumentOutOfRangeException(nameof(targetCount));
        }

        if (targetCount < _entries.Count) {
            _entries.RemoveRange(targetCount, _entries.Count - targetCount);
        }
    }

    public void PushUndo(TurnSnapshot snapshot) => _undoStack.Push(snapshot);
    public TurnSnapshot? PopUndo() => _undoStack.TryPop(out var snapshot) ? snapshot : null;

    // Compaction rewrites history, invalidating snapshot indices.
    public void DiscardUndo() => _undoStack.Clear();

    public IReadOnlyList<TranscriptItem> Snapshot() => [
        .. _entries.Select(entry => new TranscriptItem(
            entry.Kind,
            entry.Text.ToString(),
            entry.IsStreaming,
            entry.Expanded))
    ];

    private void FinalizeActiveEntries() {
        _assistant?.IsStreaming = false;
        _reasoning?.IsStreaming = false;
        RemoveEmpty(_assistant);
        RemoveEmpty(_reasoning);
        _assistant = null;
        _reasoning = null;
    }

    private LiveEntry EnsureAssistant() {
        if (_assistant is { IsStreaming: true }) {
            return _assistant;
        }

        _assistant = new LiveEntry(TranscriptEntryKind.Assistant) { IsStreaming = true };
        _entries.Add(_assistant);
        _reasoning = null;
        return _assistant;
    }

    private void AddEntry(TranscriptEntryKind kind, string text) => _entries.Add(new LiveEntry(kind, text));

    private void RemoveEmpty(LiveEntry? entry) {
        if (entry is null || entry.Text.Length > 0) return;

        _entries.Remove(entry);
    }

    private sealed class LiveEntry(TranscriptEntryKind kind, string text = "") {
        public TranscriptEntryKind Kind { get; } = kind;
        public StringBuilder Text { get; } = new(text);
        public bool IsStreaming { get; set; }
        public bool Expanded { get; } = kind != TranscriptEntryKind.Reasoning;
    }
}
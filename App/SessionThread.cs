using System.Diagnostics;
using System.Text;
using Kite.Agent;
using Kite.Sessions;
using Kite.Ui;

namespace Kite.App;

internal sealed class SessionThread(Session session) {
    private readonly List<LiveEntry> _entries = [];
    private LiveEntry? _assistant;
    private LiveEntry? _reasoning;

    public Session Session { get; } = session;

    public List<ConversationMessage> Messages => Session.Messages;

    public bool IsStreaming { get; private set; }

    public string CurrentAssistantText => _assistant?.Text.ToString() ?? string.Empty;

    public CancellationTokenSource? TurnCancellation { get; set; }

    public Task? TurnTask { get; set; }

    private Stopwatch? TurnElapsed { get; set; }

    public static SessionThread Open(Session session) {
        var thread = new SessionThread(session);
        foreach (var message in session.Messages) {
            if (message.Type == ConversationMessage.FunctionCallOutputType) continue;

            if (message.Type == ConversationMessage.FunctionCallType) {
                thread.AddEntry(TranscriptEntryKind.Tool, message.Name ?? "tool");
                continue;
            }

            if (message.Type is not null) {
                throw new InvalidOperationException($"Unknown session message type: {message.Type}");
            }

            switch (message.Role) {
                case "user":
                    thread.AddEntry(TranscriptEntryKind.User, message.Content);
                    break;
                case "assistant":
                    thread.AddEntry(TranscriptEntryKind.Assistant, message.Content);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown session message role: {message.Role}");
            }
        }

        return thread;
    }

    public void AddUserMessage(string text) => AddEntry(TranscriptEntryKind.User, text);

    public void StartTurn() {
        if (IsStreaming) throw new InvalidOperationException("Session is already streaming");

        IsStreaming = true;
        TurnElapsed = Stopwatch.StartNew();
        _assistant = new LiveEntry(TranscriptEntryKind.Assistant) { IsStreaming = true };
        _entries.Add(_assistant);
        _reasoning = null;
    }

    public void AppendAssistant(string text) {
        EnsureAssistant().Text.Append(text);
    }

    public void AppendReasoning(string text) {
        EnsureAssistant();
        if (_reasoning is null) {
            _reasoning = new LiveEntry(TranscriptEntryKind.Reasoning) { IsStreaming = true };
            _entries.Insert(_entries.IndexOf(_assistant!), _reasoning);
        }

        _reasoning.Text.Append(text);
    }

    public void AddTool(string line) {
        if (_assistant is not null) _assistant.IsStreaming = false;
        if (_reasoning is not null) _reasoning.IsStreaming = false;
        RemoveEmpty(_assistant);
        RemoveEmpty(_reasoning);
        _assistant = null;
        _reasoning = null;
        AddEntry(TranscriptEntryKind.Tool, line);
    }

    public TimeSpan CompleteTurn() {
        if (!IsStreaming || TurnElapsed is null) {
            throw new InvalidOperationException("Session has no streaming turn");
        }

        var elapsed = TurnElapsed.Elapsed;
        IsStreaming = false;
        _assistant?.IsStreaming = false;
        _reasoning?.IsStreaming = false;
        RemoveEmpty(_assistant);
        RemoveEmpty(_reasoning);
        _assistant = null;
        _reasoning = null;
        TurnElapsed = null;
        return elapsed;
    }

    public void AddInfo(string text) => AddEntry(TranscriptEntryKind.Info, text);

    public void AddError(string text) => AddEntry(TranscriptEntryKind.Error, text);

    public IReadOnlyList<TranscriptItem> Snapshot() => [
        .. _entries
            .Select(entry => new TranscriptItem(
                entry.Kind,
                entry.Text.ToString(),
                entry.IsStreaming,
                entry.Expanded))
    ];

    private LiveEntry EnsureAssistant() {
        if (_assistant is { IsStreaming: true }) return _assistant;

        _assistant = new LiveEntry(TranscriptEntryKind.Assistant) { IsStreaming = true };
        _entries.Add(_assistant);
        _reasoning = null;
        return _assistant;
    }

    private void AddEntry(TranscriptEntryKind kind, string text) {
        _entries.Add(new LiveEntry(kind, text));
    }

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
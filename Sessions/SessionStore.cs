using System.Text;
using System.Text.Json;
using Kite.Agent;
using Kite.Configuration;

namespace Kite.Sessions;

public sealed class SessionStore(string workspace) {
    private const string SessionLineType = "session";
    private const string StateLineType = "state";
    private const string MessagesLineType = "messages";

    // The state line sits at a fixed offset and is overwritten in place, so it must never change
    // length. Widest content: 33 byte timestamp, 31 byte decimal cost and four 10 digit counters,
    // which stays under this; the remainder is JSON insignificant trailing whitespace.
    private const int StateRecordBytes = 256;

    private const int TitleLength = 48;

    // ponytail: one process-wide lock; use per-session locks if append throughput matters.
    private static readonly Lock FileGate = new();
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly string _directory = Path.Combine(Paths.DataDirectory, "sessions");

    public string Workspace { get; } = Path.GetFullPath(workspace);

    public IReadOnlyList<Session> List() {
        if (!Directory.Exists(_directory)) return [];

        return [
            .. Directory.EnumerateFiles(_directory, "*.jsonl", SearchOption.TopDirectoryOnly)
                .Select(Load)
                .Where(session => session.Workspace.Equals(Workspace, PathComparison))
                .OrderByDescending(session => session.UpdatedAt)
        ];
    }

    public Session Create() {
        var now = DateTimeOffset.UtcNow;
        var session = new Session {
            Id = Guid.NewGuid().ToString("N"),
            Workspace = Workspace,
            CreatedAt = now,
            UpdatedAt = now
        };
        EnsureDirectory();
        var path = SessionPath(session.Id);
        WriteNewFile(path, FileMode.CreateNew, session);
        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return session;
    }

    public void Append(Session session, IReadOnlyList<ConversationMessage> messages) {
        if (messages.Count == 0) {
            throw new ArgumentException("At least one message is required.", nameof(messages));
        }

        ValidateIdentity(session, checkWorkspace: true);
        foreach (var message in messages) ValidateMessage(message, session.Id);

        lock (FileGate) {
            var path = SessionPath(session.Id);
            if (!File.Exists(path)) {
                throw new InvalidOperationException($"Session file does not exist: {path}");
            }

            var updatedAt = DateTimeOffset.UtcNow;
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) {
                WriteLine(stream, MessagesLine(updatedAt, messages));
            }

            session.Messages.AddRange(messages);
            session.UpdatedAt = updatedAt;
        }
    }

    public void Delete(Session session) {
        ValidateIdentity(session, checkWorkspace: true);

        lock (FileGate) {
            var path = SessionPath(session.Id);
            if (!File.Exists(path)) {
                throw new InvalidOperationException($"Session file does not exist: {path}");
            }

            File.Delete(path);
        }
    }

    // ponytail: in-place overwriting beats appending a superseded snapshot every turn. The counters
    // live only here, so a checksum could detect a torn write but never repair one.
    public void SaveCost(Session session) {
        ValidateIdentity(session, checkWorkspace: true);
        lock (FileGate) {
            var path = SessionPath(session.Id);
            if (!File.Exists(path)) {
                throw new InvalidOperationException($"Session file does not exist: {path}");
            }

            var header = File.ReadLines(path).FirstOrDefault()
                         ?? throw new InvalidOperationException($"Session file is empty: {path}");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            stream.Seek(Utf8.GetByteCount(header) + 1, SeekOrigin.Begin);
            stream.Write(StateRecord(session));
        }
    }

    public void Truncate(Session session, int targetMessageCount) {
        ValidateIdentity(session, checkWorkspace: true);
        if (targetMessageCount < 0 || targetMessageCount > session.Messages.Count) {
            throw new ArgumentOutOfRangeException(nameof(targetMessageCount));
        }

        if (targetMessageCount == session.Messages.Count) return;

        lock (FileGate) {
            var path = SessionPath(session.Id);
            if (!File.Exists(path)) {
                throw new InvalidOperationException($"Session file does not exist: {path}");
            }

            session.Messages.RemoveRange(targetMessageCount, session.Messages.Count - targetMessageCount);
            session.UpdatedAt = DateTimeOffset.UtcNow;
            WriteNewFile(path, FileMode.Create, session);
        }
    }

    public void RewriteMessages(Session session, IReadOnlyList<ConversationMessage> messages) {
        if (messages.Count == 0) {
            throw new ArgumentException("At least one message is required.", nameof(messages));
        }

        ValidateIdentity(session, checkWorkspace: true);
        foreach (var message in messages) {
            ValidateMessage(message, session.Id);
        }

        lock (FileGate) {
            var path = SessionPath(session.Id);
            if (!File.Exists(path)) {
                throw new InvalidOperationException($"Session file does not exist: {path}");
            }

            session.Messages.Clear();
            session.Messages.AddRange(messages);
            session.UpdatedAt = DateTimeOffset.UtcNow;
            WriteNewFile(path, FileMode.Create, session);
        }
    }

    public static string Label(Session session, bool active) {
        var firstUserMessage = session.Messages.FirstOrDefault(message => message.Role == "user")?.Content;
        string title;
        if (string.IsNullOrWhiteSpace(firstUserMessage)) {
            title = "New session";
        } else if (CompactionService.IsCompactionSummary(firstUserMessage)) {
            var summary = CompactionService.ExtractSummary(firstUserMessage);
            title = CompactionService.ExtractGoalTitle(summary);
        } else {
            title = new string(firstUserMessage.Where(character => !char.IsControl(character)).ToArray()).Trim();
        }

        if (title.Length == 0) {
            title = "New session";
        }

        if (title.Length > TitleLength) {
            title = $"{title[..(TitleLength - 1)]}…";
        }

        return $"{(active ? "* " : "  ")}{title}\t{session.Id[..8]}";
    }

    // ponytail: any unreadable line fails the whole load, and List() loads every session, so one
    // damaged file blocks them all. A half-written final line is left unhandled until observed.
    private Session Load(string path) {
        Session? session = null;
        var lineNumber = 0;
        foreach (var text in File.ReadLines(path)) {
            lineNumber += 1;
            if (string.IsNullOrWhiteSpace(text)) {
                throw new InvalidOperationException($"Session file contains an empty line: {path}:{lineNumber}");
            }

            var line = ParseLine(text, path, lineNumber);
            switch (line.Type) {
                case SessionLineType:
                    if (session is not null || lineNumber != 1) {
                        throw new InvalidOperationException(
                            $"Session file has multiple session headers: {path}:{lineNumber}");
                    }

                    session = new Session {
                        Id = Required(line.Id, "id", path, lineNumber),
                        Workspace = Required(line.Workspace, "workspace", path, lineNumber),
                        CreatedAt = Required(line.CreatedAt, "createdAt", path, lineNumber)
                    };
                    break;
                case StateLineType:
                    if (session is null) {
                        throw new InvalidOperationException(
                            $"Session state appears before the header: {path}:{lineNumber}");
                    }

                    session.UpdatedAt = Required(line.UpdatedAt, "updatedAt", path, lineNumber);
                    session.Cost = line.Cost;
                    session.PromptTokens = line.PromptTokens;
                    session.CompletionTokens = line.CompletionTokens;
                    session.CachedTokens = line.CachedTokens;
                    session.LastPromptTokens = line.LastPromptTokens;
                    break;
                case MessagesLineType:
                    if (session is null) {
                        throw new InvalidOperationException(
                            $"Session messages appear before the header: {path}:{lineNumber}");
                    }

                    var batch = line.Messages
                                ?? throw new InvalidOperationException(
                                    $"Session message batch is missing: {path}:{lineNumber}");
                    if (batch.Count == 0) {
                        throw new InvalidOperationException($"Session message batch is empty: {path}:{lineNumber}");
                    }

                    session.UpdatedAt = Required(line.UpdatedAt, "updatedAt", path, lineNumber);
                    foreach (var message in batch) {
                        ValidateMessage(message, session.Id);
                        session.Messages.Add(message);
                    }

                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown session line type '{line.Type}': {path}:{lineNumber}");
            }
        }

        if (session is null) {
            throw new InvalidOperationException($"Session file has no header: {path}");
        }

        var fileId = Path.GetFileNameWithoutExtension(path);
        if (!session.Id.Equals(fileId, StringComparison.Ordinal)) {
            throw new InvalidOperationException($"Session file name does not match its id: {path}");
        }

        ValidateIdentity(session, checkWorkspace: false);
        return session;
    }

    private static SessionLine ParseLine(string text, string path, int lineNumber) {
        try {
            return JsonSerializer.Deserialize(text, KiteJsonContext.Default.SessionLine)
                   ?? throw new InvalidOperationException($"Session line is empty: {path}:{lineNumber}");
        } catch (JsonException ex) {
            throw new InvalidOperationException($"Session line is invalid: {path}:{lineNumber}: {ex.Message}", ex);
        }
    }

    private static string Required(string? value, string name, string path, int lineNumber) =>
        value ?? throw new InvalidOperationException($"Session line is missing '{name}': {path}:{lineNumber}");

    private static DateTimeOffset Required(DateTimeOffset? value, string name, string path, int lineNumber) =>
        value ?? throw new InvalidOperationException($"Session line is missing '{name}': {path}:{lineNumber}");

    private void ValidateIdentity(Session session, bool checkWorkspace) {
        if (!Guid.TryParseExact(session.Id, "N", out _)) {
            throw new InvalidOperationException($"Session has an invalid id: {session.Id}");
        }

        if (string.IsNullOrWhiteSpace(session.Workspace)) {
            throw new InvalidOperationException($"Session '{session.Id}' has no workspace");
        }

        if (checkWorkspace && !session.Workspace.Equals(Workspace, PathComparison)) {
            throw new InvalidOperationException($"Session '{session.Id}' belongs to another workspace");
        }

        if (session.CreatedAt == default || session.UpdatedAt == default || session.UpdatedAt < session.CreatedAt) {
            throw new InvalidOperationException($"Session '{session.Id}' has invalid timestamps");
        }
    }

    private static void ValidateMessage(ConversationMessage? message, string sessionId) {
        if (message?.Role is null || message.Content is null) {
            throw new InvalidOperationException($"Session '{sessionId}' contains an invalid message");
        }

        switch (message.Type) {
            case null when message.Role is not ("user" or "assistant") ||
                           message.CallId is not null || message.Name is not null || message.Arguments is not null:
                throw new InvalidOperationException($"Session '{sessionId}' contains an invalid message");
            case null:
                return;
            case ConversationMessage.FunctionCallType when message.Role.Length > 0 || message.Content.Length > 0 ||
                                                           string.IsNullOrEmpty(message.CallId) ||
                                                           string.IsNullOrEmpty(message.Name) ||
                                                           message.Arguments is null:
                throw new InvalidOperationException($"Session '{sessionId}' contains an invalid function call");
            case ConversationMessage.FunctionCallType:
                return;
        }

        if (message.Type != ConversationMessage.FunctionCallOutputType) {
            throw new InvalidOperationException(
                $"Session '{sessionId}' contains an unknown message type: {message.Type}");
        }

        if (message.Role.Length > 0 || string.IsNullOrEmpty(message.CallId)) {
            throw new InvalidOperationException($"Session '{sessionId}' contains an invalid function call output");
        }
    }

    private string SessionPath(string id) => Guid.TryParseExact(id, "N", out _)
        ? Path.Combine(_directory, $"{id}.jsonl")
        : throw new InvalidOperationException($"Session has an invalid id: {id}");


    private void EnsureDirectory() {
        Directory.CreateDirectory(Paths.DataDirectory);
        Directory.CreateDirectory(_directory);
        if (OperatingSystem.IsWindows()) return;

        const UnixFileMode permissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(Paths.DataDirectory, permissions);
        File.SetUnixFileMode(_directory, permissions);
    }

    private static SessionLine HeaderLine(Session session) => new() {
        Type = SessionLineType,
        Id = session.Id,
        Workspace = session.Workspace,
        CreatedAt = session.CreatedAt
    };

    private static SessionLine StateLine(Session session) => new() {
        Type = StateLineType,
        UpdatedAt = session.UpdatedAt,
        Cost = session.Cost,
        PromptTokens = session.PromptTokens,
        CompletionTokens = session.CompletionTokens,
        CachedTokens = session.CachedTokens,
        LastPromptTokens = session.LastPromptTokens
    };

    private static SessionLine MessagesLine(DateTimeOffset updatedAt, IReadOnlyList<ConversationMessage> messages) =>
        new() {
            Type = MessagesLineType,
            UpdatedAt = updatedAt,
            Messages = [.. messages]
        };

    private static byte[] StateRecord(Session session) {
        var json = JsonSerializer.Serialize(StateLine(session), KiteJsonContext.Default.SessionLine);
        var content = Utf8.GetByteCount(json);
        if (content + 1 > StateRecordBytes) {
            throw new InvalidOperationException(
                $"Session state needs {content + 1} bytes but only {StateRecordBytes} are reserved");
        }

        var record = new byte[StateRecordBytes];
        record.AsSpan().Fill((byte)' ');
        Utf8.GetBytes(json, record);
        record[^1] = (byte)'\n';
        return record;
    }

    /// <summary>Writes a whole session file: the identity line, the fixed width state line, then one messages line.</summary>
    private static void WriteNewFile(string path, FileMode mode, Session session) {
        using var stream = new FileStream(path, mode, FileAccess.Write, FileShare.Read);
        WriteLine(stream, HeaderLine(session));
        stream.Write(StateRecord(session));
        if (session.Messages.Count > 0) {
            WriteLine(stream, MessagesLine(session.UpdatedAt, session.Messages));
        }
    }

    private static void WriteLine(Stream stream, SessionLine line) {
        stream.Write(Utf8.GetBytes(JsonSerializer.Serialize(line, KiteJsonContext.Default.SessionLine)));
        stream.WriteByte((byte)'\n');
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
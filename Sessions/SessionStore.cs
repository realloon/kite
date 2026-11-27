using System.Text;
using System.Text.Json;
using Kite.Agent;
using Kite.Config;

namespace Kite.Sessions;

internal sealed class SessionStore(string workspace) {
    private const string SessionLineType = "session";
    private const string StateLineType = "state";
    private const string MessagesLineType = "messages";

    // Fixed-width state line for in-place updates; padded with trailing spaces.
    private const int StateRecordBytes = 256;
    private const int TitleLength = 48;

    // ponytail: one process-wide lock; use per-session locks if append throughput matters.
    private static readonly Lock FileGate = new();
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly string _directory = Paths.Sessions;

    public string Workspace { get; } = Path.GetFullPath(workspace);

    public IReadOnlyList<Session> List() {
        if (!Directory.Exists(_directory)) {
            return [];
        }

        return [
            .. Directory.EnumerateFiles(_directory, "*.jsonl", SearchOption.TopDirectoryOnly)
                .Select(Load)
                .Where(session => session.Workspace.Equals(Workspace, Paths.Comparison))
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
        Paths.EnsurePrivateDirectory(Paths.DataDirectory);
        Paths.EnsurePrivateDirectory(_directory);
        var path = SessionPath(session.Id);
        WriteNewFile(path, FileMode.CreateNew, session);
        Paths.SetPrivateFileMode(path);
        return session;
    }

    public void Append(Session session, IReadOnlyList<ConversationMessage> messages) {
        if (messages.Count == 0) {
            throw new ArgumentException("At least one message is required.", nameof(messages));
        }

        ValidateIdentity(session, checkWorkspace: true);
        lock (FileGate) {
            var path = RequireExistingPath(session.Id);
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
            File.Delete(RequireExistingPath(session.Id));
        }
    }

    // ponytail: in-place overwriting beats appending a superseded snapshot every turn. The counters
    // live only here, so a checksum could detect a torn write but never repair one.
    public void SaveCost(Session session) {
        ValidateIdentity(session, checkWorkspace: true);
        lock (FileGate) {
            var path = RequireExistingPath(session.Id);
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
            var path = RequireExistingPath(session.Id);
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
        lock (FileGate) {
            var path = RequireExistingPath(session.Id);
            session.Messages.Clear();
            session.Messages.AddRange(messages);
            session.UpdatedAt = DateTimeOffset.UtcNow;
            WriteNewFile(path, FileMode.Create, session);
        }
    }

    public static string Label(Session session, bool active) {
        var firstUserMessage = session.Messages.OfType<UserMessage>().FirstOrDefault()?.Content;
        string title;
        if (string.IsNullOrWhiteSpace(firstUserMessage)) {
            title = "New session";
        } else if (CompactionService.IsCompactionSummary(firstUserMessage)) {
            title = CompactionService.ExtractGoalTitle(CompactionService.ExtractSummary(firstUserMessage));
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
                    foreach (var dto in batch) {
                        if (dto is null) {
                            throw new InvalidOperationException($"Session '{session.Id}' contains an invalid message");
                        }

                        session.Messages.Add(dto.ToMessage(session.Id));
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
            return JsonSerializer.Deserialize(text, SessionJsonContext.Default.SessionLine)
                   ?? throw new InvalidOperationException($"Session line is empty: {path}:{lineNumber}");
        } catch (JsonException ex) {
            throw new InvalidOperationException($"Session line is invalid: {path}:{lineNumber}: {ex.Message}", ex);
        }
    }

    private static T Required<T>(T? value, string name, string path, int lineNumber) where T : class =>
        value ?? throw new InvalidOperationException($"Session line is missing '{name}': {path}:{lineNumber}");

    private static T Required<T>(T? value, string name, string path, int lineNumber) where T : struct =>
        value ?? throw new InvalidOperationException($"Session line is missing '{name}': {path}:{lineNumber}");

    private void ValidateIdentity(Session session, bool checkWorkspace) {
        if (!Guid.TryParseExact(session.Id, "N", out _)) {
            throw new InvalidOperationException($"Session has an invalid id: {session.Id}");
        }

        if (string.IsNullOrWhiteSpace(session.Workspace)) {
            throw new InvalidOperationException($"Session '{session.Id}' has no workspace");
        }

        if (checkWorkspace && !session.Workspace.Equals(Workspace, Paths.Comparison)) {
            throw new InvalidOperationException($"Session '{session.Id}' belongs to another workspace");
        }

        if (session.CreatedAt == default || session.UpdatedAt == default || session.UpdatedAt < session.CreatedAt) {
            throw new InvalidOperationException($"Session '{session.Id}' has invalid timestamps");
        }
    }

    private string SessionPath(string id) => Path.Combine(_directory, $"{id}.jsonl");

    private string RequireExistingPath(string id) {
        var path = SessionPath(id);
        return File.Exists(path)
            ? path
            : throw new InvalidOperationException($"Session file does not exist: {path}");
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
            Messages = [.. messages.Select(MessageDto.FromMessage)]
        };

    private static byte[] StateRecord(Session session) {
        var json = JsonSerializer.Serialize(StateLine(session), SessionJsonContext.Default.SessionLine);
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

    private static void WriteNewFile(string path, FileMode mode, Session session) {
        using var stream = new FileStream(path, mode, FileAccess.Write, FileShare.Read);
        WriteLine(stream, HeaderLine(session));
        stream.Write(StateRecord(session));
        if (session.Messages.Count > 0) {
            WriteLine(stream, MessagesLine(session.UpdatedAt, session.Messages));
        }
    }

    private static void WriteLine(Stream stream, SessionLine line) {
        stream.Write(Utf8.GetBytes(JsonSerializer.Serialize(line, SessionJsonContext.Default.SessionLine)));
        stream.WriteByte((byte)'\n');
    }
}
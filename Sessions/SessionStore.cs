using System.Text;
using System.Text.Json;
using Kite.Agent;
using Kite.Configuration;

namespace Kite.Sessions;

public sealed class Session {
    public string Id { get; init; } = string.Empty;

    public string Workspace { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ConversationMessage> Messages { get; init; } = [];
}

internal sealed class SessionLine {
    public string Type { get; set; } = string.Empty;

    public string? Id { get; set; }

    public string? Workspace { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public List<ConversationMessage>? Messages { get; set; }
}

public sealed class SessionStore {
    private const string SessionLineType = "session";
    private const string MessagesLineType = "messages";
    private const int TitleLength = 48;
    // ponytail: one process-wide lock; use per-session locks if append throughput matters.
    private static readonly Lock FileGate = new();
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly string _workspace;
    private readonly string _directory = Path.Combine(KiteConfig.DataDirectory, "sessions");

    public SessionStore(string workspace) {
        _workspace = Path.GetFullPath(workspace);
    }

    public IReadOnlyList<Session> List() {
        if (!Directory.Exists(_directory)) return [];

        return Directory.EnumerateFiles(_directory, "*.jsonl", SearchOption.TopDirectoryOnly)
            .Select(Load)
            .Where(session => string.Equals(session.Workspace, _workspace, PathComparison))
            .OrderByDescending(session => session.UpdatedAt)
            .ToArray();
    }

    public Session Create() {
        var now = DateTimeOffset.UtcNow;
        var session = new Session {
            Id = Guid.NewGuid().ToString("N"),
            Workspace = _workspace,
            CreatedAt = now,
            UpdatedAt = now
        };
        EnsureDirectory();
        var path = SessionPath(session.Id);
        WriteLine(
            path,
            new SessionLine {
                Type = SessionLineType,
                Id = session.Id,
                Workspace = session.Workspace,
                CreatedAt = session.CreatedAt,
                UpdatedAt = session.UpdatedAt
            },
            FileMode.CreateNew);
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
            WriteLine(
                path,
                new SessionLine {
                    Type = MessagesLineType,
                    UpdatedAt = updatedAt,
                    Messages = [.. messages]
                },
                FileMode.Append);
            session.Messages.AddRange(messages);
            session.UpdatedAt = updatedAt;
        }
    }

    public static string Label(Session session, bool active) {
        var firstUserMessage = session.Messages.FirstOrDefault(message => message.Role == "user")?.Content;
        var title = string.IsNullOrWhiteSpace(firstUserMessage)
            ? "New session"
            : new string(firstUserMessage.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (title.Length == 0) title = "New session";
        if (title.Length > TitleLength) title = $"{title[..(TitleLength - 1)]}…";

        return $"{(active ? "* " : "  ")}{title} · {session.Id[..8]}";
    }

    private Session Load(string path) {
        Session? session = null;
        var lineNumber = 0;
        foreach (var text in File.ReadLines(path)) {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(text)) {
                throw new InvalidOperationException($"Session file contains an empty line: {path}:{lineNumber}");
            }

            var line = ParseLine(text, path, lineNumber);
            switch (line.Type) {
                case SessionLineType:
                    if (session is not null || lineNumber != 1) {
                        throw new InvalidOperationException($"Session file has multiple session headers: {path}:{lineNumber}");
                    }

                    session = new Session {
                        Id = Required(line.Id, "id", path, lineNumber),
                        Workspace = Required(line.Workspace, "workspace", path, lineNumber),
                        CreatedAt = Required(line.CreatedAt, "createdAt", path, lineNumber),
                        UpdatedAt = Required(line.UpdatedAt, "updatedAt", path, lineNumber)
                    };
                    break;
                case MessagesLineType:
                    if (session is null) {
                        throw new InvalidOperationException($"Session messages appear before the header: {path}:{lineNumber}");
                    }

                    if (line.Messages is not { Count: > 0 } messages) {
                        throw new InvalidOperationException($"Session message batch is empty: {path}:{lineNumber}");
                    }

                    session.UpdatedAt = Required(line.UpdatedAt, "updatedAt", path, lineNumber);
                    foreach (var message in messages) {
                        ValidateMessage(message, session.Id);
                        session.Messages.Add(message);
                    }

                    break;
                default:
                    throw new InvalidOperationException($"Unknown session line type '{line.Type}': {path}:{lineNumber}");
            }
        }

        if (session is null) {
            throw new InvalidOperationException($"Session file has no header: {path}");
        }

        var fileId = Path.GetFileNameWithoutExtension(path);
        if (!string.Equals(session.Id, fileId, StringComparison.Ordinal)) {
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

    private static DateTimeOffset Required(
        DateTimeOffset? value,
        string name,
        string path,
        int lineNumber) =>
        value ?? throw new InvalidOperationException($"Session line is missing '{name}': {path}:{lineNumber}");

    private void ValidateIdentity(Session session, bool checkWorkspace) {
        if (!Guid.TryParseExact(session.Id, "N", out _)) {
            throw new InvalidOperationException($"Session has an invalid id: {session.Id}");
        }

        if (string.IsNullOrWhiteSpace(session.Workspace)) {
            throw new InvalidOperationException($"Session '{session.Id}' has no workspace");
        }

        if (checkWorkspace && !string.Equals(session.Workspace, _workspace, PathComparison)) {
            throw new InvalidOperationException($"Session '{session.Id}' belongs to another workspace");
        }

        if (session.CreatedAt == default || session.UpdatedAt == default || session.UpdatedAt < session.CreatedAt) {
            throw new InvalidOperationException($"Session '{session.Id}' has invalid timestamps");
        }
    }

    private static void ValidateMessage(ConversationMessage? message, string sessionId) {
        if (message is null || message.Role is null || message.Content is null) {
            throw new InvalidOperationException($"Session '{sessionId}' contains an invalid message");
        }

        if (message.Type is null) {
            if (message.Role is not ("user" or "assistant") ||
                message.CallId is not null || message.Name is not null || message.Arguments is not null) {
                throw new InvalidOperationException($"Session '{sessionId}' contains an invalid message");
            }

            return;
        }

        if (message.Type == ConversationMessage.FunctionCallType) {
            if (message.Role.Length > 0 || message.Content.Length > 0 ||
                string.IsNullOrEmpty(message.CallId) || string.IsNullOrEmpty(message.Name) ||
                message.Arguments is null) {
                throw new InvalidOperationException($"Session '{sessionId}' contains an invalid function call");
            }

            return;
        }

        if (message.Type == ConversationMessage.FunctionCallOutputType) {
            if (message.Role.Length > 0 || string.IsNullOrEmpty(message.CallId)) {
                throw new InvalidOperationException($"Session '{sessionId}' contains an invalid function call output");
            }

            return;
        }

        throw new InvalidOperationException($"Session '{sessionId}' contains an unknown message type: {message.Type}");
    }

    private string SessionPath(string id) {
        if (!Guid.TryParseExact(id, "N", out _)) {
            throw new InvalidOperationException($"Session has an invalid id: {id}");
        }

        return Path.Combine(_directory, $"{id}.jsonl");
    }

    private void EnsureDirectory() {
        Directory.CreateDirectory(KiteConfig.DataDirectory);
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows()) {
            var permissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(KiteConfig.DataDirectory, permissions);
            File.SetUnixFileMode(_directory, permissions);
        }
    }

    private static void WriteLine(string path, SessionLine line, FileMode mode) {
        using var stream = new FileStream(path, mode, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, Utf8);
        writer.WriteLine(JsonSerializer.Serialize(line, KiteJsonContext.Default.SessionLine));
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

using System.Text;
using Kite.Commands;

namespace Kite.Ui;

/// <summary>
/// One transient full-screen chat view. The terminal is only a frame sink;
/// transcript state lives here.
/// </summary>
public sealed class FullScreenChatView(string? modelLabel = null) : IChatView, IDisposable {
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(8);

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TerminalSession _terminal = new();
    private readonly List<TranscriptEntry> _entries = [];
    private readonly InputLine _input = new();

    private string _footerText = modelLabel ?? "Not connected";
    private string _statusText = string.Empty;
    private Task? _renderTask;
    private TranscriptEntry? _assistant;
    private TranscriptEntry? _reasoning;
    private int _width;
    private int _totalLines;
    private int _scrollFromBottom;
    private string[]? _lastFrameRows;
    private bool _streaming;
    private bool _reasoningExpanded;
    private bool _inputMasked;
    private bool _commandCompletionEnabled;
    private bool _commandCompletionDismissed;
    private string? _commandCompletionQuery;
    private int _commandCompletionIndex;
    private IReadOnlyList<string>? _choiceOptions;
    private int _choiceIndex;
    private bool _choiceCanDelete;
    private bool _dirty = true;
    private bool _started;
    private bool _disposed;

    public void ShowWelcome() {
        lock (_gate) {
            ThrowIfDisposed();
            if (_started) {
                throw new InvalidOperationException("TUI is already running");
            }

            _terminal.Enter();
            _started = true;
            _width = Console.WindowWidth;
            AddEntryLocked(TranscriptEntryKind.Info, "kite");
            _renderTask = Task.Run(RenderLoopAsync, _lifetime.Token);
        }
    }

    public void AddUserMessage(string text) {
        lock (_gate) {
            ThrowIfDisposed();
            AddEntryLocked(TranscriptEntryKind.User, text);
            FollowBottomLocked();
        }
    }

    public void LoadTranscript(IReadOnlyList<TranscriptItem> items, bool streaming) {
        lock (_gate) {
            ThrowIfDisposed();
            ClearTranscriptLocked();
            AddEntryLocked(TranscriptEntryKind.Info, "kite");
            foreach (var item in items) {
                var entry = new TranscriptEntry(
                    item.Kind,
                    item.Expanded) {
                    IsStreaming = item.IsStreaming
                };
                entry.SetScreenWidth(Math.Max(1, _width));
                entry.Append(item.Text);
                _entries.Add(entry);
                _totalLines += entry.DisplayLineCount + 1;
                if (item is { IsStreaming: true, Kind: TranscriptEntryKind.Assistant }) {
                    _assistant = entry;
                }

                if (item is { IsStreaming: true, Kind: TranscriptEntryKind.Reasoning }) {
                    _reasoning = entry;
                }
            }

            _streaming = streaming;
            _statusText = streaming ? "Generating · Esc to stop" : string.Empty;
            _reasoningExpanded = _entries
                .Where(entry => entry.Kind == TranscriptEntryKind.Reasoning)
                .Select(entry => entry.Expanded)
                .FirstOrDefault();
            FollowBottomLocked();
        }
    }

    public void StartAssistantTurn() {
        lock (_gate) {
            ThrowIfDisposed();
            if (!_started || _streaming) {
                throw new InvalidOperationException("Cannot start a new assistant turn");
            }

            _streaming = true;
            _statusText = "Generating · Esc to stop";
            _assistant = AddEntryLocked(TranscriptEntryKind.Assistant, string.Empty);
            _assistant.IsStreaming = true;
            EnsureReasoningLocked(_assistant);
            FollowBottomLocked();
        }
    }

    public void AppendAssistantChunk(string chunk) {
        lock (_gate) {
            ThrowIfDisposed();
            var assistant = EnsureAssistantLocked();
            AppendLocked(assistant, chunk);
        }
    }

    public void AppendReasoningChunk(string chunk) {
        lock (_gate) {
            ThrowIfDisposed();
            var assistant = EnsureAssistantLocked();
            AppendLocked(EnsureReasoningLocked(assistant), chunk);
        }
    }

    public void AppendToolLine(string line) {
        lock (_gate) {
            ThrowIfDisposed();
            if (_assistant is not null) {
                _assistant.IsStreaming = false;
                if (_assistant.Content.Length == 0) {
                    RemoveEntryLocked(_assistant);
                }

                _assistant = null;
            }

            if (_reasoning is not null) {
                _reasoning.IsStreaming = false;
                if (_reasoning.Content.Length == 0) {
                    RemoveEntryLocked(_reasoning);
                }

                _reasoning = null;
            }

            AddEntryLocked(TranscriptEntryKind.Tool, line);
            FollowBottomLocked();
        }
    }

    public void EndAssistantTurn() {
        lock (_gate) {
            if (!_streaming) {
                throw new InvalidOperationException("No assistant turn to end");
            }

            _streaming = false;
            _statusText = string.Empty;
            _assistant?.IsStreaming = false;
            _reasoning?.IsStreaming = false;
            RemoveEmptyEntryLocked(_assistant);
            RemoveEmptyEntryLocked(_reasoning);
            _assistant = null;
            _reasoning = null;
            _dirty = true;
        }
    }

    public void WriteError(string message) => AddEntry(TranscriptEntryKind.Error, message);

    public void WriteInfo(string message) => AddEntry(TranscriptEntryKind.Info, message);

    public async Task<string?> ReadSecretAsync(
        string prompt,
        CancellationToken cancellationToken) {
        WriteInfo(prompt);
        return await ReadInputAsync(true, cancellationToken, false);
    }

    public async Task<ChoiceResult?> ReadChoiceAsync(
        string prompt,
        IReadOnlyList<string> choices,
        CancellationToken cancellationToken,
        bool allowDelete = false) {
        if (choices.Count == 0) {
            throw new ArgumentException("At least one choice is required.", nameof(choices));
        }

        WriteInfo(prompt);
        using var choiceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deleteIndex = -1;
        Action<int>? requestDelete = allowDelete
            ? index => deleteIndex = index
            : null;
        lock (_gate) {
            ThrowIfDisposed();
            _inputMasked = false;
            _commandCompletionEnabled = false;
            _choiceOptions = [.. choices];
            _choiceIndex = 0;
            _choiceCanDelete = allowDelete;
            _dirty = true;
        }

        try {
            var selected = await _input.ReadAsync(
                choiceCancellation.Token,
                masked: false,
                MarkDirty,
                key => HandleChoiceKey(key, choiceCancellation, requestDelete),
                ScrollByMouse,
                recordHistory: false);
            if (deleteIndex >= 0) return new ChoiceResult(deleteIndex, true);
            return selected is null ? null : new ChoiceResult(_choiceIndex, false);
        } catch (OperationCanceledException) when (choiceCancellation.IsCancellationRequested) {
            return deleteIndex >= 0 ? new ChoiceResult(deleteIndex, true) : null;
        } finally {
            lock (_gate) {
                _choiceOptions = null;
                _choiceIndex = 0;
                _choiceCanDelete = false;
                _dirty = true;
            }
        }
    }

    public void SetModelName(string modelName) {
        lock (_gate) {
            _footerText = CleanLabel(modelName);
            _dirty = true;
        }
    }

    public Task<string?> ReadUserInputAsync(
        CancellationToken cancellationToken,
        Func<bool>? onEscape = null) =>
        ReadInputAsync(false, cancellationToken, true, onEscape);

    public void Dispose() {
        Task? renderTask;

        lock (_gate) {
            if (_disposed) {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
            renderTask = _renderTask;
        }

        try {
            WaitForTask(renderTask);
        } finally {
            _terminal.Dispose();
            _lifetime.Dispose();
        }
    }

    private async Task<string?> ReadInputAsync(
        bool masked,
        CancellationToken cancellationToken,
        bool commandCompletion,
        Func<bool>? onEscape = null) {
        lock (_gate) {
            ThrowIfDisposed();
            _inputMasked = masked;
            _commandCompletionEnabled = commandCompletion;
            _commandCompletionDismissed = false;
            _commandCompletionQuery = null;
            _commandCompletionIndex = 0;
            _dirty = true;
        }

        try {
            return await _input.ReadAsync(
                cancellationToken,
                masked,
                MarkDirty,
                key => HandleInputKey(key, onEscape),
                ScrollByMouse);
        } finally {
            lock (_gate) {
                _inputMasked = false;
                _commandCompletionEnabled = false;
                _commandCompletionDismissed = false;
                _commandCompletionQuery = null;
                _commandCompletionIndex = 0;
                _dirty = true;
            }
        }
    }

    private async Task RenderLoopAsync() {
        var cancellationToken = _lifetime.Token;
        var lastWidth = -1;
        var lastHeight = -1;
        using var timer = new PeriodicTimer(FrameInterval);

        try {
            while (true) {
                string? frame = null;
                lock (_gate) {
                    var width = Console.WindowWidth;
                    var height = Console.WindowHeight;
                    if (width != lastWidth || height != lastHeight) {
                        ReflowLocked(width);
                        _lastFrameRows = null;
                        lastWidth = width;
                        lastHeight = height;
                        _dirty = true;
                    }

                    if (_dirty) {
                        _dirty = false;
                        frame = RenderFrameLocked(width, height);
                    }
                }

                if (frame is not null) {
                    await Console.Out.WriteAsync(frame.AsMemory(), cancellationToken);
                    await Console.Out.FlushAsync(cancellationToken);
                }

                await timer.WaitForNextTickAsync(cancellationToken);
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private string RenderFrameLocked(int width, int height) {
        var commands = GetCommandSuggestionsLocked();
        var completionLines = _choiceOptions is null
            ? BuildCommandCompletionLines(width, height, commands)
            : BuildChoiceLines(width, height, _choiceOptions);
        var inputLines = _input.DisplayLines(_inputMasked)
            .Select(line => CellTextLayout.Clip(line, width))
            .ToList();
        var fixedRows = completionLines.Count == 0
            ? inputLines.Count + 2
            : inputLines.Count + completionLines.Count + 1;
        var availableTranscriptRows = Math.Max(0, height - fixedRows);
        ClampScrollLocked(availableTranscriptRows);
        var fits = _totalLines + fixedRows <= height && _scrollFromBottom == 0;
        var transcriptRows = fits ? _totalLines : Math.Min(_totalLines, availableTranscriptRows);
        var body = TakeBodyLinesLocked(transcriptRows);
        var footerText = completionLines.Count == 0
            ? BuildFooter()
            : _choiceOptions is null
                ? "  ↑↓ Navigate   Enter Use   Esc Close"
                : _choiceCanDelete
                    ? "  ↑↓ Navigate   Enter Select   Ctrl+D Delete   Esc Cancel"
                    : "  ↑↓ Navigate   Enter Select   Esc Cancel";
        var footer = CellTextLayout.Clip(footerText, width);
        var (cursorRow, cursorColumn) = _input.CursorPosition(_inputMasked);
        cursorColumn = Math.Clamp(cursorColumn, 1, Math.Max(1, width));
        var inputRow = fits ? _totalLines + 1 : Math.Max(1, height - fixedRows + 1);
        var footerRow = fits ? _totalLines + fixedRows : height;

        var rows = new string[height];
        Array.Fill(rows, string.Empty);

        for (var row = 0; row < body.Count; row++) {
            rows[row] = body[row];
        }

        for (var row = 0; row < inputLines.Count; row++) {
            var targetRow = inputRow + row - 1;
            if (targetRow >= 0 && targetRow < rows.Length) {
                rows[targetRow] = inputLines[row];
            }
        }

        for (var row = 0; row < completionLines.Count; row++) {
            var targetRow = inputRow + row;
            if (targetRow >= 0 && targetRow < rows.Length) {
                rows[targetRow] = completionLines[row];
            }
        }

        rows[footerRow - 1] = $"\e[2m{footer}\e[0m";

        var frame = new StringBuilder();
        for (var row = 0; row < rows.Length; row++) {
            if (_lastFrameRows is not null && _lastFrameRows.Length == rows.Length &&
                string.Equals(_lastFrameRows[row], rows[row], StringComparison.Ordinal)) {
                continue;
            }

            frame.Append($"\e[{row + 1};1H\e[K{rows[row]}");
        }

        var cursorScreenRow = Math.Clamp(inputRow + cursorRow, 1, Math.Max(1, height));
        frame.Append($"\e[{cursorScreenRow};{cursorColumn}H");
        _lastFrameRows = rows;
        return frame.ToString();
    }

    private List<SlashCommand> GetCommandSuggestionsLocked() {
        var query = _input.Text;
        if (!_commandCompletionEnabled || _commandCompletionDismissed) return [];

        if (!string.Equals(_commandCompletionQuery, query, StringComparison.Ordinal)) {
            _commandCompletionQuery = query;
            _commandCompletionIndex = 0;
        }

        if (query.Length == 0 || query[0] != '/' || query.Contains(' ')) return [];

        var matches = SlashCommands.All
            .Where(command => command.MatchesPrefix(query))
            .ToList();

        _commandCompletionIndex = matches.Count == 0 ? 0 : Math.Clamp(_commandCompletionIndex, 0, matches.Count - 1);

        return matches;
    }

    private List<string> BuildCommandCompletionLines(
        int width,
        int height,
        IReadOnlyList<SlashCommand> commands) {
        if (commands.Count == 0) return [];

        var visibleCount = Math.Min(commands.Count, Math.Max(0, height - 5));
        if (visibleCount == 0) return [];

        var start = Math.Clamp(
            _commandCompletionIndex - visibleCount / 2,
            0,
            commands.Count - visibleCount);
        var nameWidth = commands.Max(command => command.Name.Length) + 2;
        var lines = new List<string>(visibleCount + 2) {
            $"\e[2m{new string('─', width)}\e[0m"
        };

        for (var row = 0; row < visibleCount; row++) {
            var index = start + row;
            var command = commands[index];
            var name = CellTextLayout.Clip($"  {command.Name.PadRight(nameWidth)}", width);
            var description = CellTextLayout.Clip(
                command.Description,
                Math.Max(0, width - CellTextLayout.CellWidth(name)));
            lines.Add(index == _commandCompletionIndex
                ? $"\e[1m{name}\e[0m\e[1;90m{description}\e[0m"
                : $"{name}\e[1;90m{description}\e[0m");
        }

        lines.Add($"\e[2m{new string('─', width)}\e[0m");
        return lines;
    }

    private List<string> BuildChoiceLines(
        int width,
        int height,
        IReadOnlyList<string> choices) {
        var visibleCount = Math.Min(choices.Count, Math.Max(0, height - 5));
        if (visibleCount == 0) return [];

        var start = Math.Clamp(
            _choiceIndex - visibleCount / 2,
            0,
            choices.Count - visibleCount);
        var lines = new List<string>(visibleCount + 2) {
            $"\e[2m{new string('─', width)}\e[0m"
        };

        for (var row = 0; row < visibleCount; row++) {
            var index = start + row;
            var plain = CellTextLayout.Clip($"  {choices[index]}", width);
            lines.Add(index == _choiceIndex
                ? $"\e[1m{plain}\e[0m"
                : $"\e[2m{plain}\e[0m");
        }

        lines.Add($"\e[2m{new string('─', width)}\e[0m");
        return lines;
    }

    private List<string> TakeBodyLinesLocked(int bodyRows) {
        ClampScrollLocked(bodyRows);

        if (_totalLines <= bodyRows) {
            var lines = new List<string>(bodyRows);
            foreach (var entry in _entries) {
                for (var index = 0; index < entry.DisplayLineCount; index++) {
                    lines.Add(FormatLine(entry, index));
                }

                lines.Add(string.Empty);
            }

            while (lines.Count < bodyRows) {
                lines.Add(string.Empty);
            }

            return lines;
        }

        var skip = _scrollFromBottom;
        var remaining = bodyRows;
        var reversed = new List<string>(Math.Min(bodyRows, _totalLines));

        for (var entryIndex = _entries.Count - 1; entryIndex >= 0 && remaining > 0; entryIndex--) {
            var entry = _entries[entryIndex];

            if (skip > 0) {
                skip -= 1;
            } else {
                reversed.Add(string.Empty);
                remaining -= 1;
            }

            for (var index = entry.DisplayLineCount - 1; index >= 0 && remaining > 0; index--) {
                if (skip > 0) {
                    skip -= 1;
                } else {
                    reversed.Add(FormatLine(entry, index));
                    remaining -= 1;
                }
            }
        }

        reversed.Reverse();
        return reversed;
    }

    private string FormatLine(TranscriptEntry entry, int index) {
        var line = entry.DisplayLine(index);
        var style = entry.Kind switch {
            TranscriptEntryKind.Reasoning => "\e[2;3m",
            TranscriptEntryKind.Tool => "\e[2m",
            TranscriptEntryKind.Info => "\e[2m",
            TranscriptEntryKind.Error => "\e[31m",
            _ => string.Empty
        };

        return style.Length == 0 ? line : $"{style}{line}\e[0m";
    }

    private string BuildFooter() {
        var footer = _footerText;
        if (_statusText.Length > 0) {
            footer += $" · {_statusText}";
        }

        return $"  {footer}";
    }

    private TranscriptEntry EnsureAssistantLocked() {
        if (_assistant is { IsStreaming: true }) {
            return _assistant;
        }

        _assistant = AddEntryLocked(TranscriptEntryKind.Assistant, string.Empty);
        _assistant.IsStreaming = true;
        _reasoning = null;
        return _assistant;
    }

    private void AppendLocked(TranscriptEntry entry, string text) {
        if (text.Length == 0) return;

        var previousLines = entry.DisplayLineCount;
        entry.Append(text);
        _totalLines += entry.DisplayLineCount - previousLines;
        _dirty = true;
    }

    private TranscriptEntry EnsureReasoningLocked(TranscriptEntry assistant) {
        if (_reasoning is not null) return _reasoning;

        _reasoning = new TranscriptEntry(
            TranscriptEntryKind.Reasoning,
            expanded: _reasoningExpanded) {
            IsStreaming = true
        };
        _reasoning.SetScreenWidth(_width);
        _entries.Insert(_entries.IndexOf(assistant), _reasoning);
        _totalLines += _reasoning.DisplayLineCount + 1;
        _dirty = true;
        return _reasoning;
    }

    private TranscriptEntry AddEntryLocked(TranscriptEntryKind kind, string text) {
        var entry = new TranscriptEntry(kind);
        entry.SetScreenWidth(Math.Max(1, _width));
        entry.Append(text);
        _entries.Add(entry);
        _totalLines += entry.DisplayLineCount + 1;
        _dirty = true;
        return entry;
    }

    private void ClearTranscriptLocked() {
        _entries.Clear();
        _assistant = null;
        _reasoning = null;
        _streaming = false;
        _statusText = string.Empty;
        _reasoningExpanded = false;
        _totalLines = 0;
        _scrollFromBottom = 0;
        _lastFrameRows = null;
    }

    private void AddEntry(TranscriptEntryKind kind, string text) {
        lock (_gate) {
            ThrowIfDisposed();
            AddEntryLocked(kind, text);
            FollowBottomLocked();
        }
    }

    private void RemoveEntryLocked(TranscriptEntry entry) {
        if (!_entries.Remove(entry)) return;

        _totalLines -= entry.DisplayLineCount + 1;
        _dirty = true;
    }

    private void RemoveEmptyEntryLocked(TranscriptEntry? entry) {
        if (entry is null || entry.Content.Length > 0) return;

        RemoveEntryLocked(entry);
    }

    private void ReflowLocked(int width) {
        _width = Math.Max(1, width);
        _totalLines = 0;
        foreach (var entry in _entries) {
            entry.SetScreenWidth(_width);
            _totalLines += entry.DisplayLineCount + 1;
        }

        ClampScrollLocked(Math.Max(0, Console.WindowHeight - 3));
    }

    private bool HandleInputKey(ConsoleKeyInfo key, Func<bool>? onEscape = null) {
        if (key.Key == ConsoleKey.Escape && onEscape?.Invoke() == true) {
            return false;
        }

        if (key.Key == ConsoleKey.O && (key.Modifiers & ConsoleModifiers.Control) != 0) {
            ToggleAllReasoning();
            return true;
        }

        lock (_gate) {
            if (_commandCompletionDismissed && key.Key is not (
                    ConsoleKey.Escape or ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.Enter)) {
                _commandCompletionDismissed = false;
            }

            var commands = GetCommandSuggestionsLocked();
            if (commands.Count <= 0) return false;

            if (key.Key == ConsoleKey.UpArrow) {
                _commandCompletionIndex = _commandCompletionIndex == 0
                    ? commands.Count - 1
                    : _commandCompletionIndex - 1;
                _dirty = true;
                return true;
            }

            if (key.Key == ConsoleKey.DownArrow) {
                _commandCompletionIndex = (_commandCompletionIndex + 1) % commands.Count;
                _dirty = true;
                return true;
            }

            if (key.Key == ConsoleKey.Escape) {
                _commandCompletionDismissed = true;
                _dirty = true;
                return true;
            }

            if (key.Key == ConsoleKey.Tab ||
                key.Key == ConsoleKey.Enter &&
                (key.Modifiers & ConsoleModifiers.Alt) == 0) {
                _input.SetText(commands[_commandCompletionIndex].Name);
                _dirty = true;
                return key.Key == ConsoleKey.Tab;
            }

            return false;
        }
    }

    private bool HandleChoiceKey(
        ConsoleKeyInfo key,
        CancellationTokenSource cancellation,
        Action<int>? requestDelete = null) {
        lock (_gate) {
            if (_choiceOptions is not { Count: > 0 } choices) return false;

            if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0) {
                cancellation.Cancel();
                return true;
            }

            if (key.Key == ConsoleKey.D && (key.Modifiers & ConsoleModifiers.Control) != 0) {
                if (requestDelete is null) return true;

                requestDelete(_choiceIndex);
                cancellation.Cancel();
                return true;
            }

            if (key.Key == ConsoleKey.UpArrow) {
                _choiceIndex = _choiceIndex == 0 ? choices.Count - 1 : _choiceIndex - 1;
                _dirty = true;
                return true;
            }

            if (key.Key == ConsoleKey.DownArrow) {
                _choiceIndex = (_choiceIndex + 1) % choices.Count;
                _dirty = true;
                return true;
            }

            if (key.Key == ConsoleKey.Escape) {
                cancellation.Cancel();
                return true;
            }

            if (key.Key == ConsoleKey.Enter &&
                (key.Modifiers & ConsoleModifiers.Alt) == 0) {
                _input.SetText(choices[_choiceIndex]);
                _dirty = true;
                return false;
            }

            return true;
        }
    }

    private void ScrollByMouse(int direction) {
        lock (_gate) {
            var bodyRows = Math.Max(1, Console.WindowHeight - 3);
            _scrollFromBottom += direction * 3;
            ClampScrollLocked(bodyRows);
            _dirty = true;
        }
    }

    private void FollowBottomLocked() {
        _scrollFromBottom = 0;
        _dirty = true;
    }

    private void ClampScrollLocked(int bodyRows) {
        _scrollFromBottom = Math.Clamp(
            _scrollFromBottom,
            0,
            Math.Max(0, _totalLines - bodyRows));
    }

    private void ToggleAllReasoning() {
        lock (_gate) {
            var expanded = !_reasoningExpanded;
            var changed = false;

            foreach (var entry in _entries.Where(entry => entry.Kind == TranscriptEntryKind.Reasoning)) {
                changed = true;
                var previousLines = entry.DisplayLineCount;
                entry.Expanded = expanded;
                _totalLines += entry.DisplayLineCount - previousLines;
            }

            if (!changed) return;

            _reasoningExpanded = expanded;
            _dirty = true;
        }
    }

    private static string CleanLabel(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

    private void MarkDirty() {
        lock (_gate) {
            _dirty = true;
        }
    }

    private void ThrowIfDisposed() {
        if (!_disposed) return;

        throw new ObjectDisposedException(nameof(FullScreenChatView));
    }

    private static void WaitForTask(Task? task) {
        if (task is null) return;

        try {
            task.GetAwaiter().GetResult();
        } catch (OperationCanceledException) { }
    }

    private sealed class TerminalSession : IDisposable {
        private bool _entered;
        private bool _previousTreatControlCAsInput;

        public void Enter() {
            if (Console.IsInputRedirected || Console.IsOutputRedirected) {
                throw new InvalidOperationException("Full-screen TUI requires an interactive terminal");
            }

            _previousTreatControlCAsInput = Console.TreatControlCAsInput;
            Console.TreatControlCAsInput = true;
            Console.Out.Write("\e[?1049h\e[?1000h\e[?1006h\e[2J\e[H\e[?25h");
            Console.Out.Flush();
            _entered = true;
        }

        public void Dispose() {
            if (!_entered) return;

            try {
                Console.Out.Write("\e[0m\e[?1000l\e[?1006l\e[?25h\e[r\e[?1049l");
                Console.Out.Flush();
            } finally {
                Console.TreatControlCAsInput = _previousTreatControlCAsInput;
                _entered = false;
            }
        }
    }
}
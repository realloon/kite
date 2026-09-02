using System.Diagnostics;
using System.Text;

namespace Kite.Ui;

/// <summary>
/// One transient full-screen chat view. The terminal is only a frame sink;
/// transcript state lives here and is ready to be persisted later.
/// </summary>
public sealed class FullScreenChatView(string? modelLabel = null) : IChatView, IDisposable {
    // Target 120Hz so normal scheduler jitter still leaves a 60Hz floor.
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(8);

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TerminalSession _terminal = new();
    private readonly List<TranscriptEntry> _entries = [];
    private readonly InputLine _input = new();

    private string _footerText = modelLabel ?? "未连接";
    private string _statusText = string.Empty;
    private Task? _renderTask;
    private Task? _interruptTask;
    private CancellationTokenSource? _turnCts;
    private Stopwatch? _turnElapsed;
    private TranscriptEntry? _assistant;
    private TranscriptEntry? _reasoning;
    private int _nextId;
    private int _width;
    private int _totalLines;
    private int _scrollFromBottom;
    private string[]? _lastFrameRows;
    private bool _streaming;
    private bool _inputMasked;
    private bool _dirty = true;
    private bool _started;
    private bool _disposed;

    public CancellationToken TurnCancellationToken {
        get {
            lock (_gate) {
                return _turnCts?.Token ?? CancellationToken.None;
            }
        }
    }

    public void ShowWelcome() {
        lock (_gate) {
            ThrowIfDisposed();
            if (_started) {
                throw new InvalidOperationException("TUI 已经启动");
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

    public void ResetTranscript() {
        lock (_gate) {
            ThrowIfDisposed();
            if (_streaming) {
                throw new InvalidOperationException("无法清空正在生成的对话");
            }

            _entries.Clear();
            _assistant = null;
            _reasoning = null;
            _nextId = 0;
            _totalLines = 0;
            _scrollFromBottom = 0;
            _lastFrameRows = null;
            AddEntryLocked(TranscriptEntryKind.Info, "kite");
        }
    }

    public void StartAssistantTurn() {
        lock (_gate) {
            ThrowIfDisposed();
            if (!_started || _streaming) {
                throw new InvalidOperationException("无法开始新的 assistant turn");
            }

            var turnCts = new CancellationTokenSource();
            _turnCts = turnCts;
            _turnElapsed = Stopwatch.StartNew();
            _streaming = true;
            _statusText = "生成中 · Esc 停止";
            _assistant = AddEntryLocked(TranscriptEntryKind.Assistant, string.Empty);
            _assistant.IsStreaming = true;
            _reasoning = null;
            FollowBottomLocked();
            _interruptTask = Task.Run(() => WatchInterruptAsync(turnCts), _lifetime.Token);
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
            if (_reasoning is null) {
                _reasoning = new TranscriptEntry(++_nextId, TranscriptEntryKind.Reasoning, expanded: false) {
                    IsStreaming = true
                };
                _reasoning.SetScreenWidth(_width);
                var assistantIndex = _entries.IndexOf(assistant);
                _entries.Insert(assistantIndex, _reasoning);
                _totalLines += _reasoning.DisplayLineCount + 1;
                _dirty = true;
            }

            AppendLocked(_reasoning, chunk);
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

    public async Task<TurnMeta> EndAssistantTurnAsync(
        bool interrupted,
        int promptTokens,
        int completionTokens) {
        CancellationTokenSource? turnCts;
        Task? interruptTask;
        TimeSpan elapsed;

        lock (_gate) {
            if (!_streaming || _turnCts is null || _turnElapsed is null) {
                throw new InvalidOperationException("没有正在结束的 assistant turn");
            }

            _streaming = false;
            _statusText = string.Empty;
            _assistant?.IsStreaming = false;
            _reasoning?.IsStreaming = false;
            elapsed = _turnElapsed.Elapsed;
            turnCts = _turnCts;
            interruptTask = _interruptTask;
        }

        await turnCts.CancelAsync();
        try {
            if (interruptTask is not null) {
                await interruptTask;
            }
        } finally {
            lock (_gate) {
                _turnCts = null;
                _interruptTask = null;
                _turnElapsed = null;
                _assistant = null;
                _reasoning = null;
                _dirty = true;
            }

            turnCts.Dispose();
        }

        var meta = new TurnMeta(elapsed, promptTokens, completionTokens, interrupted);
        WriteInfo(interrupted ? $"interrupted — {meta}" : meta.ToString());
        return meta;
    }

    public void WriteError(string message) => AddEntry(TranscriptEntryKind.Error, message);

    public void WriteInfo(string message) => AddEntry(TranscriptEntryKind.Info, message);

    public async Task<string?> ReadSecretAsync(
        string prompt,
        CancellationToken cancellationToken) {
        WriteInfo(prompt);
        return await ReadInputAsync(masked: true, cancellationToken);
    }

    public async Task<string?> ReadTextAsync(
        string prompt,
        CancellationToken cancellationToken) {
        WriteInfo(prompt);
        return await ReadInputAsync(masked: false, cancellationToken);
    }

    public void SetModelName(string modelName) {
        lock (_gate) {
            _footerText = CleanLabel(modelName);
            _dirty = true;
        }
    }

    public Task<string?> ReadUserInputAsync(CancellationToken cancellationToken) =>
        ReadInputAsync(masked: false, cancellationToken);

    public void Dispose() {
        Task? renderTask;
        Task? interruptTask;

        lock (_gate) {
            if (_disposed) {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
            _turnCts?.Cancel();
            renderTask = _renderTask;
            interruptTask = _interruptTask;
        }

        try {
            WaitForTask(renderTask);
            WaitForTask(interruptTask);
        } finally {
            _terminal.Dispose();
            _lifetime.Dispose();
        }
    }

    private async Task<string?> ReadInputAsync(bool masked, CancellationToken cancellationToken) {
        lock (_gate) {
            ThrowIfDisposed();
            _inputMasked = masked;
            _dirty = true;
        }

        try {
            return await _input.ReadAsync(
                cancellationToken,
                masked,
                MarkDirty,
                HandleInputKey,
                ScrollByMouse);
        } finally {
            lock (_gate) {
                _inputMasked = false;
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
        // The transcript owns the first blank row before the input. The input,
        // its trailing blank row, and the footer follow it until the screen is
        // full; after that only the latest transcript rows remain visible.
        var availableTranscriptRows = Math.Max(0, height - 3);
        ClampScrollLocked(availableTranscriptRows);
        var fits = _totalLines + 3 <= height && _scrollFromBottom == 0;
        var transcriptRows = fits ? _totalLines : Math.Min(_totalLines, availableTranscriptRows);
        var body = TakeBodyLinesLocked(transcriptRows);
        var input = CellTextLayout.Clip(_input.Display(_inputMasked), width);
        var footer = CellTextLayout.Clip(BuildFooter(), width);
        var cursorColumn = Math.Clamp(_input.CursorColumn(_inputMasked), 1, Math.Max(1, width));
        var inputRow = fits ? _totalLines + 1 : Math.Max(1, height - 2);
        var footerRow = fits ? _totalLines + 3 : height;

        var rows = new string[height];
        Array.Fill(rows, string.Empty);

        for (var row = 0; row < body.Count; row++) {
            rows[row] = body[row];
        }

        rows[inputRow - 1] = input;
        rows[footerRow - 1] = $"\e[2m{footer}\e[0m";

        var frame = new StringBuilder();
        for (var row = 0; row < rows.Length; row++) {
            if (_lastFrameRows is not null && _lastFrameRows.Length == rows.Length &&
                string.Equals(_lastFrameRows[row], rows[row], StringComparison.Ordinal)) {
                continue;
            }

            frame.Append($"\e[{row + 1};1H{rows[row]}\e[K");
        }

        frame.Append($"\e[{inputRow};{cursorColumn}H");
        _lastFrameRows = rows;
        return frame.ToString();
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

        // Bottom-following is the hot path. Walk backwards from the tail so a
        // long session costs only the visible rows, not the whole transcript.
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

    private TranscriptEntry AddEntryLocked(TranscriptEntryKind kind, string text) {
        var entry = new TranscriptEntry(++_nextId, kind);
        entry.SetScreenWidth(Math.Max(1, _width));
        entry.Append(text);
        _entries.Add(entry);
        _totalLines += entry.DisplayLineCount + 1;
        _dirty = true;
        return entry;
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

    private void ReflowLocked(int width) {
        _width = Math.Max(1, width);
        _totalLines = 0;
        foreach (var entry in _entries) {
            entry.SetScreenWidth(_width);
            _totalLines += entry.DisplayLineCount + 1;
        }

        ClampScrollLocked(Math.Max(0, Console.WindowHeight - 3));
    }

    private bool HandleInputKey(ConsoleKeyInfo key) {
        if (key.Key != ConsoleKey.O || (key.Modifiers & ConsoleModifiers.Control) == 0) {
            return false;
        }

        ToggleLatestReasoning();
        return true;
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

    private void ToggleLatestReasoning() {
        lock (_gate) {
            var entry = _entries.LastOrDefault(item => item.Kind == TranscriptEntryKind.Reasoning);
            if (entry is null) return;

            var previousLines = entry.DisplayLineCount;
            entry.Expanded = !entry.Expanded;
            _totalLines += entry.DisplayLineCount - previousLines;
            _dirty = true;
        }
    }

    private async Task WatchInterruptAsync(CancellationTokenSource turnCts) {
        var mouse = new MouseWheelParser();

        try {
            while (!turnCts.IsCancellationRequested) {
                if (Console.KeyAvailable) {
                    var key = Console.ReadKey(intercept: true);
                    var consumed = mouse.Consume(key, out var wheelDirection, out var replayEscape);
                    if (wheelDirection != 0) {
                        ScrollByMouse(wheelDirection);
                    }

                    if (consumed) {
                        continue;
                    }

                    if (replayEscape ||
                        key.Key == ConsoleKey.Escape ||
                        (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)) {
                        await turnCts.CancelAsync();
                        return;
                    }

                    HandleInputKey(key);
                } else if (mouse.Flush(out var escaped) && escaped) {
                    await turnCts.CancelAsync();
                    return;
                }

                await Task.Delay(8, turnCts.Token);
            }
        } catch (OperationCanceledException) when (turnCts.IsCancellationRequested) { }
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
                throw new InvalidOperationException("全屏 TUI 需要连接到交互式终端");
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
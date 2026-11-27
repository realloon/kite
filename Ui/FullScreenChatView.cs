using System.Diagnostics;
using System.Text;

namespace Kite.Ui;

internal sealed record ChoiceResult(int Index, bool DeleteRequested);

internal sealed class FullScreenChatView(string modelLabel, IReadOnlyList<(string Name, string Description)> skills)
    : IDisposable {
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(8);
    private static readonly TimeSpan BlinkHalfPeriod = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CopyFeedbackDuration = TimeSpan.FromMilliseconds(500);

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TerminalSession _terminal = new();
    private readonly List<TranscriptEntry> _entries = [];
    private readonly InputLine _input = new();
    private readonly Stopwatch _blinkStopwatch = Stopwatch.StartNew();
    private bool _blinkVisible;
    private string? _prefilledInput;
    private string _footerText = modelLabel;
    private string _statusText = string.Empty;
    private string _sessionCost = string.Empty;
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
    private int _selectedChoiceIndex;
    private string? _choiceFilterQuery;
    private bool _choiceCanDelete;
    private int _choiceDeleteIndex = -1;
    private (int Col, int Row)? _selectionStart;
    private (int Col, int Row)? _selectionEnd;
    private bool _isSelecting;
    private long _copyFeedbackExpiry;
    private string[]? _visiblePlainRows;
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

    public void AddUserMessage(string text) => AddEntry(TranscriptEntryKind.User, text);

    public void LoadTranscript(IReadOnlyList<TranscriptItem> items, bool streaming) {
        lock (_gate) {
            ThrowIfDisposed();
            ClearTranscriptLocked();
            AddEntryLocked(TranscriptEntryKind.Info, "kite");
            foreach (var item in items) {
                var entry = new TranscriptEntry(item.Kind, item.Expanded) { IsStreaming = item.IsStreaming };
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
            _statusText = streaming ? "Esc to stop" : string.Empty;
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
            _statusText = "Esc to stop";
            _assistant = AddEntryLocked(TranscriptEntryKind.Assistant, string.Empty);
            _assistant.IsStreaming = true;
            EnsureReasoningLocked(_assistant);
            FollowBottomLocked();
        }
    }

    public void AppendAssistantChunk(string chunk) {
        lock (_gate) {
            ThrowIfDisposed();
            AppendLocked(EnsureAssistantLocked(), chunk);
        }
    }

    public void AppendReasoningChunk(string chunk) {
        lock (_gate) {
            ThrowIfDisposed();
            AppendLocked(EnsureReasoningLocked(EnsureAssistantLocked()), chunk);
        }
    }

    public void AppendToolLine(string line) {
        lock (_gate) {
            ThrowIfDisposed();
            FinalizeActiveEntriesLocked();
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
            FinalizeActiveEntriesLocked();
            _dirty = true;
        }
    }

    public void WriteError(string message) => AddEntry(TranscriptEntryKind.Error, message);
    public void WriteInfo(string message) => AddEntry(TranscriptEntryKind.Info, message);

    public async Task<string?> ReadSecretAsync(string prompt, CancellationToken ct) {
        WriteInfo(prompt);
        return await ReadInputAsync(masked: true, commandCompletion: false, onEscape: null, ct);
    }

    public async Task<ChoiceResult?> ReadChoiceAsync(IReadOnlyList<string> choices, CancellationToken ct,
        bool allowDelete = false) {
        if (choices.Count == 0) {
            throw new ArgumentException("At least one choice is required.", nameof(choices));
        }

        using var choiceCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate) {
            ThrowIfDisposed();
            _inputMasked = false;
            _commandCompletionEnabled = false;
            _choiceOptions = [.. choices];
            _choiceFilterQuery = string.Empty;
            _selectedChoiceIndex = choices
                .Select((choice, index) => choice.StartsWith("* ", StringComparison.Ordinal) ? index : -1)
                .FirstOrDefault(index => index >= 0);
            _choiceIndex = _selectedChoiceIndex;
            _choiceCanDelete = allowDelete;
            _choiceDeleteIndex = -1;
            _dirty = true;
        }

        try {
            var selected = await _input.ReadAsync(
                masked: false,
                MarkDirty,
                key => HandleChoiceKey(key, choiceCancellation),
                HandleMouseEvent,
                recordHistory: false,
                choiceCancellation.Token);

            return _choiceDeleteIndex >= 0
                ? new ChoiceResult(_choiceDeleteIndex, true)
                : selected is null ? null : new ChoiceResult(_selectedChoiceIndex, false);
        } catch (OperationCanceledException) when (choiceCancellation.IsCancellationRequested) {
            return _choiceDeleteIndex >= 0 ? new ChoiceResult(_choiceDeleteIndex, true) : null;
        } finally {
            lock (_gate) {
                _choiceOptions = null;
                _choiceFilterQuery = null;
                _choiceIndex = 0;
                _selectedChoiceIndex = 0;
                _choiceCanDelete = false;
                _choiceDeleteIndex = -1;
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

    public void SetSessionCost(string cost) {
        lock (_gate) {
            _sessionCost = CleanLabel(cost);
            _dirty = true;
        }
    }

    public void SetInputText(string text) {
        lock (_gate) {
            _prefilledInput = text;
            _dirty = true;
        }
    }

    public Task<string?> ReadUserInputAsync(Func<bool> onEscape, CancellationToken ct) =>
        ReadInputAsync(masked: false, commandCompletion: true, onEscape, ct);

    public void Dispose() {
        Task? renderTask;

        lock (_gate) {
            if (_disposed) return;

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

    private async Task<string?> ReadInputAsync(bool masked, bool commandCompletion, Func<bool>? onEscape,
        CancellationToken ct) {
        string? prefill = null;
        lock (_gate) {
            ThrowIfDisposed();
            _inputMasked = masked;
            _commandCompletionEnabled = commandCompletion;
            _commandCompletionDismissed = false;
            _commandCompletionQuery = null;
            _commandCompletionIndex = 0;
            if (!masked && _prefilledInput is not null) {
                prefill = _prefilledInput;
                _prefilledInput = null;
            }

            _dirty = true;
        }

        try {
            return await _input.ReadAsync(
                masked,
                MarkDirty,
                key => HandleInputKey(key, onEscape),
                HandleMouseEvent,
                recordHistory: true,
                ct,
                initialText: prefill ?? string.Empty);
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
                    if (_copyFeedbackExpiry != 0 && Stopwatch.GetTimestamp() >= _copyFeedbackExpiry) {
                        ClearSelectionLocked();
                    }

                    if (_streaming && _blinkStopwatch.Elapsed >= BlinkHalfPeriod) {
                        _blinkStopwatch.Restart();
                        _blinkVisible = !_blinkVisible;
                        _dirty = true;
                    }

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
        int selectedIndex;
        List<SuggestionItem> menuItems;
        if (_choiceOptions is null) {
            menuItems = GetCommandSuggestionsLocked();
            selectedIndex = _commandCompletionIndex;
        } else {
            menuItems = GetChoiceSuggestionsLocked(out var choiceSelected);
            selectedIndex = choiceSelected;
        }

        var completionLines = BuildMenuLines(width, height, menuItems, selectedIndex);
        var chromeRows = completionLines.Count == 0 ? 2 : completionLines.Count + 1;
        var maxInputRows = Math.Max(1, height - chromeRows);
        var inputLayout = _input.Layout(_inputMasked, width);
        var inputOffset = Math.Max(0, inputLayout.CaretRow - maxInputRows + 1);
        var inputLines = inputLayout.Lines
            .Skip(inputOffset)
            .Take(maxInputRows)
            .Select(line => CellTextLayout.Clip(line, width))
            .ToList();
        var fixedRows = inputLines.Count + chromeRows;
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
        var cursorRow = inputLayout.CaretRow - inputOffset;
        var cursorColumn = Math.Clamp(inputLayout.CaretColumn, 1, Math.Max(1, width));
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
            var targetRow = inputRow + inputLines.Count + row - 1;
            if (targetRow >= 0 && targetRow < rows.Length) {
                rows[targetRow] = completionLines[row];
            }
        }

        rows[footerRow - 1] = $"\e[2m{footer}\e[0m";

        _visiblePlainRows = Array.ConvertAll(rows, AnsiText.Strip);

        if (_selectionStart is { } s && _selectionEnd is { } e && (s.Col != e.Col || s.Row != e.Row)) {
            var (fromRow, fromCol, toRow, toCol) = AnsiText.NormalizeSelection(s, e);
            for (var row = Math.Max(0, fromRow); row <= Math.Min(rows.Length - 1, toRow); row++) {
                var startCol = row == fromRow ? fromCol : 0;
                var endCol = row == toRow ? toCol + 1 : width;
                rows[row] = AnsiText.HighlightCellRange(rows[row], startCol, endCol);
            }
        }

        var frame = new StringBuilder();
        for (var row = 0; row < rows.Length; row++) {
            if (_lastFrameRows is not null && _lastFrameRows.Length == rows.Length &&
                _lastFrameRows[row].Equals(rows[row], StringComparison.Ordinal)) {
                continue;
            }

            frame.Append($"\e[{row + 1};1H\e[K{rows[row]}");
        }

        var cursorScreenRow = Math.Clamp(inputRow + cursorRow, 1, Math.Max(1, height));
        frame.Append($"\e[{cursorScreenRow};{cursorColumn}H");
        _lastFrameRows = rows;
        return frame.ToString();
    }

    private readonly record struct SuggestionItem(string Name, string Description);

    private List<SuggestionItem> GetCommandSuggestionsLocked() {
        var query = _input.Text;
        if (!_commandCompletionEnabled || _commandCompletionDismissed) {
            return [];
        }

        if (!string.Equals(_commandCompletionQuery, query, StringComparison.Ordinal)) {
            _commandCompletionQuery = query;
            _commandCompletionIndex = 0;
        }

        if (query.Length == 0 || query.Contains(' ')) {
            return [];
        }

        List<SuggestionItem> matches = query[0] switch {
            '/' => [
                .. SlashCommands.All
                    .Where(command => command.MatchesPrefix(query))
                    .Select(command => new SuggestionItem(command.Name, command.Description))
            ],
            '$' => [
                .. skills
                    .Where(skill => ("$" + skill.Name).StartsWith(query, StringComparison.OrdinalIgnoreCase))
                    .Select(skill => new SuggestionItem("$" + skill.Name, skill.Description))
            ],
            _ => []
        };

        _commandCompletionIndex = matches.Count == 0 ? 0 : Math.Clamp(_commandCompletionIndex, 0, matches.Count - 1);
        return matches;
    }

    private List<int> GetFilteredChoiceIndicesLocked(string query) {
        if (_choiceOptions is null) {
            return [];
        }

        if (query.Length == 0) {
            return [.. Enumerable.Range(0, _choiceOptions.Count)];
        }

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = new List<int>();
        for (var i = 0; i < _choiceOptions.Count; i++) {
            var choice = _choiceOptions[i];
            if (terms.All(term => choice.Contains(term, StringComparison.OrdinalIgnoreCase))) {
                matches.Add(i);
            }
        }

        return matches;
    }

    private List<SuggestionItem> GetChoiceSuggestionsLocked(out int selectedFilteredIndex) {
        if (_choiceOptions is null) {
            selectedFilteredIndex = -1;
            return [];
        }

        var query = _input.Text.Trim();
        var filteredIndices = GetFilteredChoiceIndicesLocked(query);

        if (!string.Equals(_choiceFilterQuery, query, StringComparison.Ordinal)) {
            _choiceFilterQuery = query;
            var newPos = filteredIndices.IndexOf(_selectedChoiceIndex);
            _choiceIndex = newPos >= 0 ? newPos : 0;
        }

        if (filteredIndices.Count == 0) {
            _choiceIndex = -1;
            selectedFilteredIndex = -1;
            return [new SuggestionItem("(no matching options)", string.Empty)];
        }

        _choiceIndex = Math.Clamp(_choiceIndex, 0, filteredIndices.Count - 1);
        _selectedChoiceIndex = filteredIndices[_choiceIndex];
        selectedFilteredIndex = _choiceIndex;

        return [.. filteredIndices.Select(index => ToSuggestion(_choiceOptions[index]))];
    }

    private static List<string> BuildMenuLines(int width, int height, IReadOnlyList<SuggestionItem> items, int selected) {
        if (items.Count == 0) {
            return [];
        }

        var visibleCount = Math.Min(items.Count, Math.Max(0, height - 5));
        if (visibleCount == 0) {
            return [];
        }

        var start = Math.Clamp(selected - visibleCount / 2, 0, items.Count - visibleCount);
        var nameWidth = items.Max(item => item.Name.Length) + 2;
        var divider = $"\e[2m{new string('─', width)}\e[0m";
        var lines = new List<string>(visibleCount + 2) { divider };

        for (var row = 0; row < visibleCount; row++) {
            var index = start + row;
            var item = items[index];
            var name = CellTextLayout.Clip($"  {item.Name.PadRight(nameWidth)}", width);
            var description = CellTextLayout.Clip(item.Description,
                Math.Max(0, width - CellTextLayout.CellWidth(name)));
            lines.Add(StyleMenuItem(
                $"{name}{description}",
                index == selected,
                nameStart: 2,
                nameEnd: 2 + item.Name.Length));
        }

        lines.Add(divider);
        return lines;
    }

    private static SuggestionItem ToSuggestion(string choice) {
        var separator = choice.IndexOf('\t');
        return separator < 0
            ? new SuggestionItem(choice, string.Empty)
            : new SuggestionItem(choice[..separator], choice[(separator + 1)..]);
    }

    private static string StyleMenuItem(string text, bool selected, int nameStart, int nameEnd) {
        if (!selected) {
            return $"\e[2;39m{text}\e[0m";
        }

        nameEnd = Math.Min(nameEnd, text.Length);
        nameStart = Math.Min(nameStart, nameEnd);
        return $"\e[1;90m{text[..nameStart]}\e[0m\e[1;39m{text[nameStart..nameEnd]}\e[0m\e[1;90m{text[nameEnd..]}\e[0m";
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
        var line = entry.DisplayLine(index, _blinkVisible);
        if (entry.Kind == TranscriptEntryKind.Tool) {
            return FormatToolLine(line);
        }

        var style = entry.Kind switch {
            TranscriptEntryKind.Reasoning or TranscriptEntryKind.Info => "\e[2m",
            TranscriptEntryKind.Error => "\e[31m",
            _ => string.Empty
        };

        return style.Length == 0 ? line : $"{style}{line}\e[0m";
    }

    private static string FormatToolLine(string line) {
        var marker = line.IndexOf("• ", StringComparison.Ordinal);
        if (marker < 0) {
            return $"\e[2;39m{line}\e[0m";
        }

        var detail = line.IndexOf(' ', marker + 2);
        var prefix = $"\e[2;39m{line[..marker]}\e[0m";
        return detail < 0
            ? $"{prefix}\e[1;90m{line[marker..]}\e[0m"
            : $"{prefix}\e[1;90m{line[marker..detail]}\e[0m\e[2;39m{line[detail..]}\e[0m";
    }

    private string BuildFooter() {
        var footer = _statusText.Length > 0 ? $"{_footerText}  {_statusText}" : _footerText;
        if (_sessionCost.Length == 0) {
            return $"  {footer}";
        }

        var gap = Math.Max(1, _width - CellTextLayout.CellWidth(footer) - CellTextLayout.CellWidth(_sessionCost) - 4);
        return $"  {footer}{new string(' ', gap)}{_sessionCost}";
    }

    private void FinalizeActiveEntriesLocked() {
        _assistant?.IsStreaming = false;
        _reasoning?.IsStreaming = false;
        RemoveEmptyEntryLocked(_assistant);
        RemoveEmptyEntryLocked(_reasoning);
        _assistant = null;
        _reasoning = null;
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
        if (_reasoning is not null) {
            return _reasoning;
        }

        _reasoning = new TranscriptEntry(TranscriptEntryKind.Reasoning, expanded: _reasoningExpanded) {
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

    private void RemoveEmptyEntryLocked(TranscriptEntry? entry) {
        if (entry is null || entry.Content.Length > 0 || !_entries.Remove(entry)) return;

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

    private bool HandleInputKey(ConsoleKeyInfo key, Func<bool>? onEscape) {
        lock (_gate) {
            if (_selectionStart is not null || _copyFeedbackExpiry != 0) {
                ClearSelectionLocked();
            }
        }

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
            if (commands.Count == 0) {
                return false;
            }

            switch (key.Key) {
                case ConsoleKey.UpArrow:
                    _commandCompletionIndex = StepIndex(_commandCompletionIndex, commands.Count, -1);
                    _dirty = true;
                    return true;
                case ConsoleKey.DownArrow:
                    _commandCompletionIndex = StepIndex(_commandCompletionIndex, commands.Count, 1);
                    _dirty = true;
                    return true;
                case ConsoleKey.Escape:
                    _commandCompletionDismissed = true;
                    _dirty = true;
                    return true;
                case ConsoleKey.Tab:
                case ConsoleKey.Enter when (key.Modifiers & ConsoleModifiers.Alt) == 0:
                    _input.SetText(commands[_commandCompletionIndex].Name);
                    _dirty = true;
                    return key.Key == ConsoleKey.Tab;
                default:
                    return false;
            }
        }
    }

    private bool HandleChoiceKey(ConsoleKeyInfo key, CancellationTokenSource ct) {
        lock (_gate) {
            if (_choiceOptions is not { Count: > 0 } choices) {
                return false;
            }

            var ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
            if ((key.Key == ConsoleKey.C && ctrl) || key.Key == ConsoleKey.Escape) {
                ct.Cancel();
                return true;
            }

            var filteredIndices = GetFilteredChoiceIndicesLocked(_input.Text.Trim());

            switch (key.Key) {
                case ConsoleKey.D when ctrl:
                    if (_choiceCanDelete && _choiceIndex >= 0 && _choiceIndex < filteredIndices.Count) {
                        _choiceDeleteIndex = filteredIndices[_choiceIndex];
                        ct.Cancel();
                    }

                    return true;
                case ConsoleKey.UpArrow:
                    StepChoiceLocked(filteredIndices, -1);
                    return true;
                case ConsoleKey.DownArrow:
                    StepChoiceLocked(filteredIndices, 1);
                    return true;
                case ConsoleKey.Tab:
                    StepChoiceLocked(filteredIndices, (key.Modifiers & ConsoleModifiers.Shift) != 0 ? -1 : 1);
                    return true;
                case ConsoleKey.Enter when (key.Modifiers & ConsoleModifiers.Alt) != 0:
                    return true;
                case ConsoleKey.Enter:
                    if (_choiceIndex < 0 || _choiceIndex >= filteredIndices.Count) {
                        return true;
                    }

                    _selectedChoiceIndex = filteredIndices[_choiceIndex];
                    _input.SetText(choices[_selectedChoiceIndex]);
                    _dirty = true;
                    return false;
                default:
                    return false;
            }
        }
    }

    private void StepChoiceLocked(List<int> filteredIndices, int delta) {
        if (filteredIndices.Count == 0) return;

        _choiceIndex = StepIndex(_choiceIndex, filteredIndices.Count, delta);
        _selectedChoiceIndex = filteredIndices[_choiceIndex];
        _dirty = true;
    }

    private static int StepIndex(int current, int count, int delta) =>
        ((current < 0 ? 0 : current) + delta + count) % count;

    private void HandleMouseEvent(TerminalMouseEvent mouse) {
        lock (_gate) {
            var width = Math.Max(1, _width);
            var height = Math.Max(1, Console.WindowHeight);
            var col = Math.Clamp(mouse.Column - 1, 0, width - 1);
            var row = Math.Clamp(mouse.Row - 1, 0, height - 1);

            switch (mouse.Kind) {
                case MouseEventKind.WheelUp:
                    ClearSelectionLocked();
                    ScrollLocked(3);
                    break;
                case MouseEventKind.WheelDown:
                    ClearSelectionLocked();
                    ScrollLocked(-3);
                    break;
                case MouseEventKind.Down:
                    ClearSelectionLocked();
                    _selectionStart = (col, row);
                    _selectionEnd = (col, row);
                    _isSelecting = true;
                    _dirty = true;
                    break;
                case MouseEventKind.Drag:
                    if (_isSelecting) {
                        _selectionEnd = (col, row);
                        _dirty = true;
                    }

                    break;
                case MouseEventKind.Up:
                    if (_isSelecting) {
                        _selectionEnd = (col, row);
                        _isSelecting = false;
                        CopySelectedTextLocked();
                    }

                    break;
            }
        }
    }

    private void ScrollLocked(int delta) {
        var bodyRows = Math.Max(1, Console.WindowHeight - 3);
        _scrollFromBottom += delta;
        ClampScrollLocked(bodyRows);
        _dirty = true;
    }

    private void CopySelectedTextLocked() {
        if (_selectionStart is not { } start || _selectionEnd is not { } end ||
            (start.Col == end.Col && start.Row == end.Row)) {
            ClearSelectionLocked();
            return;
        }

        var (fromRow, fromCol, toRow, toCol) = AnsiText.NormalizeSelection(start, end);
        if (_visiblePlainRows is null || _visiblePlainRows.Length == 0) return;

        var sb = new StringBuilder();
        for (var r = fromRow; r <= toRow; r++) {
            if (r < 0 || r >= _visiblePlainRows.Length) continue;

            var line = _visiblePlainRows[r];
            if (string.IsNullOrEmpty(line)) {
                if (r < toRow && sb.Length > 0) {
                    sb.Append('\n');
                }

                continue;
            }

            var startCol = r == fromRow ? fromCol : 0;
            var endCol = r == toRow ? toCol + 1 : int.MaxValue;
            var segment = AnsiText.ExtractCellRange(line, startCol, endCol);
            if (segment.Length == 0) continue;

            if (sb.Length > 0 && sb[^1] != '\n') {
                sb.Append('\n');
            }

            sb.Append(segment);
        }

        var text = sb.ToString().TrimEnd();
        if (text.Length > 0) {
            Clipboard.SetText(text);
            _statusText = "Copied to clipboard";
            _copyFeedbackExpiry = Stopwatch.GetTimestamp() +
                                  (long)(Stopwatch.Frequency * CopyFeedbackDuration.TotalSeconds);
            _dirty = true;
        } else {
            ClearSelectionLocked();
        }
    }

    private void ClearSelectionLocked() {
        _copyFeedbackExpiry = 0;
        _selectionStart = null;
        _selectionEnd = null;
        _isSelecting = false;
        if (_statusText.Equals("Copied to clipboard", StringComparison.Ordinal)) {
            _statusText = string.Empty;
        }

        _dirty = true;
    }

    private void FollowBottomLocked() {
        _scrollFromBottom = 0;
        _dirty = true;
    }

    private void ClampScrollLocked(int bodyRows) {
        _scrollFromBottom = Math.Clamp(_scrollFromBottom, 0, Math.Max(0, _totalLines - bodyRows));
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
            FollowBottomLocked();
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
            Console.Out.Write("\e[?1049h\e[?1002h\e[?1006h\e[2J\e[H\e[?25h");
            Console.Out.Flush();
            _entered = true;
        }

        public void Dispose() {
            if (!_entered) return;

            try {
                Console.Out.Write("\e[0m\e[?1002l\e[?1006l\e[?25h\e[r\e[?1049l");
                Console.Out.Flush();
            } finally {
                Console.TreatControlCAsInput = _previousTreatControlCAsInput;
                _entered = false;
            }
        }
    }
}
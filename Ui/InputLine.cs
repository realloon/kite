using System.Diagnostics;

namespace Kite.Ui;

internal sealed class InputLine {
    private const string InputPrefix = "┃ ";
    private static readonly int PrefixWidth = CellTextLayout.CellWidth(InputPrefix);

    private readonly Lock _gate = new();
    private readonly List<string> _history = [];
    private string _text = string.Empty;
    private string _historySnapshot = string.Empty;
    private int _historyIndex;
    private int _caret;

    internal string Text {
        get {
            lock (_gate) {
                return _text;
            }
        }
    }

    public async Task<string?> ReadAsync(bool masked, Action onChanged, Func<ConsoleKeyInfo, bool>? onSpecialKey,
        Action<TerminalMouseEvent>? onMouseEvent, bool recordHistory, CancellationToken ct, string initialText = "") {
        Reset();
        if (initialText.Length > 0) {
            SetText(initialText);
        }

        onChanged();
        var inputParser = new TerminalInputParser();

        while (!ct.IsCancellationRequested) {
            if (!Console.KeyAvailable) {
                if (inputParser.Flush()) {
                    HandleEscape();
                    onChanged();
                }

                await Task.Delay(8, ct);
                continue;
            }

            var key = Console.ReadKey(intercept: true);
            var consumed = inputParser.Consume(
                key,
                out var mouseEvent,
                out var replayEscape,
                out var decodedKey);
            if (decodedKey is { } decoded) {
                key = decoded;
                consumed = false;
            }

            if (replayEscape) {
                HandleEscape();
            }

            if (mouseEvent.Kind != MouseEventKind.None) {
                onMouseEvent?.Invoke(mouseEvent);
            }

            if (consumed || onSpecialKey?.Invoke(key) == true) {
                onChanged();
                continue;
            }

            string? result = null;
            var completed = false;
            lock (_gate) {
                var ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
                var alt = (key.Modifiers & ConsoleModifiers.Alt) != 0;
                var wordMod = ctrl || alt;

                switch (key.Key) {
                    case ConsoleKey.Enter when alt:
                        InsertLocked("\n");
                        break;
                    case ConsoleKey.Enter:
                        result = _text;
                        if (result.Length > 0 && !masked && recordHistory) {
                            _history.Add(result);
                        }

                        completed = true;
                        break;
                    case ConsoleKey.C when ctrl:
                        completed = true;
                        break;
                    case ConsoleKey.Escape:
                        ClearTextLocked();
                        break;
                    case ConsoleKey.LeftArrow when wordMod:
                    case ConsoleKey.B when alt:
                        _caret = FindWordBoundaryLeftLocked(_caret);
                        break;
                    case ConsoleKey.RightArrow when wordMod:
                    case ConsoleKey.F when alt:
                        _caret = FindWordBoundaryRightLocked(_caret);
                        break;
                    case ConsoleKey.Backspace when wordMod:
                    case ConsoleKey.W when ctrl:
                        DeleteWordBackwardLocked();
                        break;
                    case ConsoleKey.Delete when wordMod:
                    case ConsoleKey.D when alt:
                        DeleteWordForwardLocked();
                        break;
                    case ConsoleKey.Backspace when _caret > 0:
                        _text = _text.Remove(_caret - 1, 1);
                        _caret -= 1;
                        break;
                    case ConsoleKey.Delete when _caret < _text.Length:
                        _text = _text.Remove(_caret, 1);
                        break;
                    case ConsoleKey.LeftArrow when _caret > 0:
                        _caret -= 1;
                        break;
                    case ConsoleKey.RightArrow when _caret < _text.Length:
                        _caret += 1;
                        break;
                    case ConsoleKey.Home:
                    case ConsoleKey.A when ctrl:
                        _caret = 0;
                        break;
                    case ConsoleKey.End:
                    case ConsoleKey.E when ctrl:
                        _caret = _text.Length;
                        break;
                    case ConsoleKey.UpArrow:
                        MoveHistoryLocked(-1);
                        break;
                    case ConsoleKey.DownArrow:
                        MoveHistoryLocked(1);
                        break;
                    default:
                        if (key.Key == ConsoleKey.Spacebar || !char.IsControl(key.KeyChar)) {
                            InsertLocked(key.Key == ConsoleKey.Spacebar ? " " : key.KeyChar.ToString());
                        }

                        break;
                }
            }

            onChanged();
            if (!completed) continue;

            Reset();
            onChanged();
            return result;
        }

        Reset();
        onChanged();
        return null;

        void HandleEscape() {
            if (onSpecialKey?.Invoke(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false)) == true) {
                return;
            }

            lock (_gate) {
                ClearTextLocked();
            }
        }
    }

    internal void SetText(string text) {
        lock (_gate) {
            _text = text;
            _caret = text.Length;
            _historyIndex = _history.Count;
            _historySnapshot = string.Empty;
        }
    }

    public InputLayout Layout(bool masked, int width) {
        lock (_gate) {
            width = Math.Max(1, width);
            var available = Math.Max(1, width - PrefixWidth);
            var display = masked ? Mask(_text) : _text;
            var lines = Wrap(display, width);
            var caretLines = Wrap(display[.._caret], width);
            var caretWidth = CellTextLayout.CellWidth(caretLines[^1]) - PrefixWidth;
            var caretRow = caretLines.Count - 1;
            var caretColumn = PrefixWidth + caretWidth + 1;

            // A full row's caret goes to the next row (before a newline: last column), created when nothing follows.
            if (caretWidth >= available) {
                if (_caret < display.Length && display[_caret] == '\n') {
                    caretColumn = width;
                } else {
                    caretRow += 1;
                    caretColumn = PrefixWidth + 1;
                    if (caretRow >= lines.Count) {
                        lines.Add(InputPrefix);
                    }
                }
            }

            return new InputLayout(lines, caretRow, caretColumn);
        }
    }

    private static string Mask(string text) {
        var chars = text.ToCharArray();
        for (var index = 0; index < chars.Length; index++) {
            if (chars[index] != '\n') {
                chars[index] = '•';
            }
        }

        return new string(chars);
    }

    private static List<string> Wrap(string text, int width) {
        var layout = new CellTextLayout();
        layout.SetWidth(width - PrefixWidth);
        layout.Append(text);
        var lines = new List<string>(layout.LineCount);
        for (var index = 0; index < layout.LineCount; index++) {
            lines.Add(InputPrefix + layout.GetLine(index));
        }

        return lines;
    }

    private void Reset() {
        lock (_gate) {
            ClearTextLocked();
            _historyIndex = _history.Count;
            _historySnapshot = string.Empty;
        }
    }

    private void ClearTextLocked() {
        _text = string.Empty;
        _caret = 0;
    }

    private void InsertLocked(string value) {
        _text = _text.Insert(_caret, value);
        _caret += value.Length;
    }

    private void MoveHistoryLocked(int direction) {
        if (_history.Count == 0) return;

        if (_historyIndex == _history.Count) {
            _historySnapshot = _text;
        }

        var next = Math.Clamp(_historyIndex + direction, 0, _history.Count);
        if (next == _historyIndex) return;

        _historyIndex = next;
        _text = next == _history.Count ? _historySnapshot : _history[next];
        _caret = _text.Length;
    }

    private void DeleteWordBackwardLocked() {
        if (_caret <= 0) return;

        var target = FindWordBoundaryLeftLocked(_caret);
        _text = _text.Remove(target, _caret - target);
        _caret = target;
    }

    private void DeleteWordForwardLocked() {
        if (_caret >= _text.Length) return;

        var target = FindWordBoundaryRightLocked(_caret);
        _text = _text.Remove(_caret, target - _caret);
    }

    private int FindWordBoundaryLeftLocked(int start) {
        var index = start;
        while (index > 0 && char.IsWhiteSpace(_text[index - 1])) {
            index -= 1;
        }

        if (index == 0) {
            return 0;
        }

        var targetClass = GetCharClass(_text[index - 1]);
        while (index > 0 && GetCharClass(_text[index - 1]) == targetClass) {
            index -= 1;
        }

        return index;
    }

    private int FindWordBoundaryRightLocked(int start) {
        var index = start;
        while (index < _text.Length && char.IsWhiteSpace(_text[index])) {
            index += 1;
        }

        if (index >= _text.Length) {
            return _text.Length;
        }

        var targetClass = GetCharClass(_text[index]);
        while (index < _text.Length && GetCharClass(_text[index]) == targetClass) {
            index += 1;
        }

        return index;
    }

    private static CharCategory GetCharClass(char c) =>
        char.IsWhiteSpace(c) ? CharCategory.Whitespace
        : char.IsLetterOrDigit(c) || c == '_' || c >= 0x0080 ? CharCategory.Word
        : CharCategory.Punctuation;

    private enum CharCategory {
        Whitespace,
        Word,
        Punctuation
    }
}

internal readonly record struct InputLayout(IReadOnlyList<string> Lines, int CaretRow, int CaretColumn);

internal enum MouseEventKind {
    None,
    Down,
    Drag,
    Up,
    WheelUp,
    WheelDown
}

internal readonly record struct TerminalMouseEvent(MouseEventKind Kind, int Column, int Row);

internal sealed class TerminalInputParser {
    private enum State {
        None,
        Escape,
        Csi,
        CsiParam,
        Button,
        Column,
        Row
    }

    private const int EscapeTimeoutMs = 50;

    private State _state;
    private long _escapeTimestamp;
    private int _button;
    private int _column;
    private int _value;
    private int _param1;

    public bool Consume(
        ConsoleKeyInfo key,
        out TerminalMouseEvent mouseEvent,
        out bool replayEscape,
        out ConsoleKeyInfo? decodedKey) {
        mouseEvent = default;
        replayEscape = false;
        decodedKey = null;

        switch (_state) {
            case State.None:
                if (key.Key != ConsoleKey.Escape) {
                    return false;
                }

                _state = State.Escape;
                _escapeTimestamp = Stopwatch.GetTimestamp();
                return true;

            case State.Escape:
                if (key.Key == ConsoleKey.Escape) {
                    _escapeTimestamp = Stopwatch.GetTimestamp();
                    return true;
                }

                if (key.KeyChar == '[') {
                    _state = State.Csi;
                    _value = 0;
                    _param1 = 0;
                    return true;
                }

                if (key.Key == ConsoleKey.Enter) {
                    return DecodeAlt(ConsoleKey.Enter, '\r', out decodedKey);
                }

                if (key.KeyChar is 'b' or 'B' || key.Key == ConsoleKey.B) {
                    return DecodeAlt(ConsoleKey.LeftArrow, '\0', out decodedKey);
                }

                if (key.KeyChar is 'f' or 'F' || key.Key == ConsoleKey.F) {
                    return DecodeAlt(ConsoleKey.RightArrow, '\0', out decodedKey);
                }

                if (key.KeyChar is 'd' or 'D' || key.Key == ConsoleKey.D) {
                    return DecodeAlt(ConsoleKey.Delete, '\0', out decodedKey);
                }

                if (key.KeyChar is '\x7f' or '\b' || key.Key == ConsoleKey.Backspace) {
                    return DecodeAlt(ConsoleKey.Backspace, '\b', out decodedKey);
                }

                Reset();
                replayEscape = true;
                return false;

            case State.Csi:
                switch (key.KeyChar) {
                    case '<':
                        _state = State.Button;
                        _value = 0;
                        return true;
                    case 'b' or 'B' or 'D' or 'd':
                        return DecodeAlt(ConsoleKey.LeftArrow, '\0', out decodedKey);
                    case 'f' or 'F' or 'C' or 'c':
                        return DecodeAlt(ConsoleKey.RightArrow, '\0', out decodedKey);
                }

                if (AppendDigit(key.KeyChar)) {
                    _state = State.CsiParam;
                    return true;
                }

                Reset();
                replayEscape = true;
                return false;

            case State.CsiParam:
                if (AppendDigit(key.KeyChar)) {
                    return true;
                }

                switch (key.KeyChar) {
                    case ';':
                        _param1 = _value;
                        _value = 0;
                        return true;
                    case 'D' or 'd':
                        return DecodeAlt(ConsoleKey.LeftArrow, '\0', out decodedKey);
                    case 'C' or 'c':
                        return DecodeAlt(ConsoleKey.RightArrow, '\0', out decodedKey);
                    case '~' when _param1 == 3 || _value == 3:
                        return DecodeAlt(ConsoleKey.Delete, '\0', out decodedKey);
                    default:
                        Reset();
                        return true;
                }

            case State.Button:
                return ConsumeMousePart(key.KeyChar, ref _button, State.Column);

            case State.Column:
                return ConsumeMousePart(key.KeyChar, ref _column, State.Row);

            case State.Row:
                if (AppendDigit(key.KeyChar)) {
                    return true;
                }

                if (key.KeyChar is 'M' or 'm') {
                    mouseEvent = DecodeMouseEvent(key.KeyChar, _button, _column, _value);
                }

                Reset();
                return true;

            default:
                throw new InvalidOperationException("Unknown terminal input state");
        }
    }

    public bool Flush() {
        if (_state == State.None || Stopwatch.GetElapsedTime(_escapeTimestamp).TotalMilliseconds < EscapeTimeoutMs) {
            return false;
        }

        var escaped = _state == State.Escape;
        Reset();
        return escaped;
    }

    private bool DecodeAlt(ConsoleKey key, char keyChar, out ConsoleKeyInfo? decodedKey) {
        decodedKey = new ConsoleKeyInfo(keyChar, key, false, true, false);
        Reset();
        return true;
    }

    private bool ConsumeMousePart(char ch, ref int target, State nextState) {
        if (AppendDigit(ch)) {
            return true;
        }

        if (ch == ';') {
            target = _value;
            _value = 0;
            _state = nextState;
            return true;
        }

        Reset();
        return true;
    }

    private static TerminalMouseEvent DecodeMouseEvent(char terminator, int button, int col, int row) =>
        (button & 64) != 0
            ? new TerminalMouseEvent((button & 1) == 0 ? MouseEventKind.WheelUp : MouseEventKind.WheelDown, col, row)
            : terminator switch {
                'M' when (button & 32) != 0 => new TerminalMouseEvent(MouseEventKind.Drag, col, row),
                'M' when (button & 3) == 0 => new TerminalMouseEvent(MouseEventKind.Down, col, row),
                'm' when (button & 3) == 0 => new TerminalMouseEvent(MouseEventKind.Up, col, row),
                _ => default
            };

    private bool AppendDigit(char value) {
        if (value is < '0' or > '9') {
            return false;
        }

        _value = Math.Min(1000, _value * 10 + value - '0');
        return true;
    }

    private void Reset() {
        _state = State.None;
        _button = 0;
        _column = 0;
        _value = 0;
        _param1 = 0;
    }
}
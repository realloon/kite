using System.Diagnostics;

namespace Kite.Ui;

public sealed class InputLine {
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
        Action<TerminalMouseEvent>? onMouseEvent, bool recordHistory, CancellationToken cancellationToken,
        string initialText = "") {
        Reset();
        if (initialText.Length > 0) {
            SetText(initialText);
        }

        onChanged();
        var inputParser = new TerminalInputParser();

        while (!cancellationToken.IsCancellationRequested) {
            if (!Console.KeyAvailable) {
                if (inputParser.Flush(out var escaped) && escaped) {
                    HandleEscape();
                    onChanged();
                }

                await Task.Delay(8, cancellationToken);
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
                switch (key.Key) {
                    case ConsoleKey.Enter when (key.Modifiers & ConsoleModifiers.Alt) != 0:
                        InsertNewline();
                        break;

                    case ConsoleKey.Enter:
                        result = _text;
                        if (result.Length > 0 && !masked && recordHistory) {
                            _history.Add(result);
                        }

                        completed = true;
                        break;

                    case ConsoleKey.C when (key.Modifiers & ConsoleModifiers.Control) != 0:
                        completed = true;
                        break;

                    case ConsoleKey.Escape:
                        _text = string.Empty;
                        _caret = 0;
                        break;

                    case ConsoleKey.LeftArrow
                        when (key.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Control)) != 0:
                    case ConsoleKey.B when (key.Modifiers & ConsoleModifiers.Alt) != 0:
                        _caret = FindWordBoundaryLeft(_caret);
                        break;

                    case ConsoleKey.RightArrow
                        when (key.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Control)) != 0:
                    case ConsoleKey.F when (key.Modifiers & ConsoleModifiers.Alt) != 0:
                        _caret = FindWordBoundaryRight(_caret);
                        break;

                    case ConsoleKey.Backspace
                        when (key.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Control)) != 0:
                    case ConsoleKey.W when (key.Modifiers & ConsoleModifiers.Control) != 0:
                        DeleteWordBackward();
                        break;

                    case ConsoleKey.Delete
                        when (key.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Control)) != 0:
                    case ConsoleKey.D when (key.Modifiers & ConsoleModifiers.Alt) != 0:
                        DeleteWordForward();
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
                    case ConsoleKey.A when (key.Modifiers & ConsoleModifiers.Control) != 0:
                        _caret = 0;
                        break;

                    case ConsoleKey.End:
                    case ConsoleKey.E when (key.Modifiers & ConsoleModifiers.Control) != 0:
                        _caret = _text.Length;
                        break;

                    case ConsoleKey.UpArrow:
                        MoveHistory(-1);
                        break;

                    case ConsoleKey.DownArrow:
                        MoveHistory(1);
                        break;

                    default:
                        InsertPrintable(key, masked);
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

            ClearText();
        }

        void ClearText() {
            lock (_gate) {
                _text = string.Empty;
                _caret = 0;
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

    public IReadOnlyList<string> DisplayLines(bool masked) {
        lock (_gate) {
            var lines = _text.Split('\n');
            for (var index = 0; index < lines.Length; index++) {
                var text = masked ? new string('•', lines[index].Length) : lines[index];
                lines[index] = $"┃ {text}";
            }

            return lines;
        }
    }

    public (int Row, int Column) CursorPosition(bool masked) {
        lock (_gate) {
            var lineStart = _caret == 0 ? 0 : _text.LastIndexOf('\n', _caret - 1) + 1;
            var visible = masked
                ? new string('•', _caret - lineStart)
                : _text[lineStart.._caret];
            return (
                _text[.._caret].Count(value => value == '\n'),
                1 + CellTextLayout.CellWidth($"┃ {visible}")
            );
        }
    }

    private void Reset() {
        lock (_gate) {
            _text = string.Empty;
            _caret = 0;
            _historyIndex = _history.Count;
            _historySnapshot = string.Empty;
        }
    }

    private void InsertPrintable(ConsoleKeyInfo key, bool masked) {
        if (key.Key != ConsoleKey.Spacebar && char.IsControl(key.KeyChar)) return;

        lock (_gate) {
            var candidate = _text.Insert(
                _caret,
                key.Key == ConsoleKey.Spacebar ? " " : key.KeyChar.ToString());
            var maxWidth = Math.Max(1, Console.WindowWidth - 2);
            if (candidate.Split('\n')
                .Select(line => masked ? new string('•', line.Length) : line)
                .Any(display => CellTextLayout.CellWidth($"┃ {display}") > maxWidth)) {
                return;
            }

            _text = candidate;
            _caret += 1;
        }
    }

    private void InsertNewline() {
        lock (_gate) {
            _text = _text.Insert(_caret, "\n");
            _caret += 1;
        }
    }

    private void MoveHistory(int direction) {
        lock (_gate) {
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
    }

    private void DeleteWordBackward() {
        if (_caret <= 0) return;

        var target = FindWordBoundaryLeft(_caret);
        var count = _caret - target;
        _text = _text.Remove(target, count);
        _caret = target;
    }

    private void DeleteWordForward() {
        if (_caret >= _text.Length) return;

        var target = FindWordBoundaryRight(_caret);
        var count = target - _caret;
        _text = _text.Remove(_caret, count);
    }

    private int FindWordBoundaryLeft(int start) {
        var index = start;
        while (index > 0 && char.IsWhiteSpace(_text[index - 1])) {
            index -= 1;
        }

        if (index == 0) return 0;

        var targetClass = GetCharClass(_text[index - 1]);
        while (index > 0 && GetCharClass(_text[index - 1]) == targetClass) {
            index -= 1;
        }

        return index;
    }

    private int FindWordBoundaryRight(int start) {
        var index = start;
        while (index < _text.Length && char.IsWhiteSpace(_text[index])) {
            index += 1;
        }

        if (index >= _text.Length) return _text.Length;

        var targetClass = GetCharClass(_text[index]);
        while (index < _text.Length && GetCharClass(_text[index]) == targetClass) {
            index += 1;
        }

        return index;
    }

    private static CharCategory GetCharClass(char c) {
        if (char.IsWhiteSpace(c)) return CharCategory.Whitespace;
        if (char.IsLetterOrDigit(c) || c == '_' || c >= 0x0080) return CharCategory.Word;
        return CharCategory.Punctuation;
    }

    private enum CharCategory {
        Whitespace,
        Word,
        Punctuation
    }
}

public enum MouseEventKind {
    None,
    Down,
    Drag,
    Up,
    WheelUp,
    WheelDown
}

public readonly record struct TerminalMouseEvent(MouseEventKind Kind, int Column, int Row);

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
                if (key.Key != ConsoleKey.Escape) return false;

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
                    decodedKey = new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, true, false);
                    Reset();
                    return true;
                }

                if (key.KeyChar is 'b' or 'B' || key.Key == ConsoleKey.B) {
                    decodedKey = new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, true, false);
                    Reset();
                    return true;
                }

                if (key.KeyChar is 'f' or 'F' || key.Key == ConsoleKey.F) {
                    decodedKey = new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, true, false);
                    Reset();
                    return true;
                }

                if (key.KeyChar is 'd' or 'D' || key.Key == ConsoleKey.D) {
                    decodedKey = new ConsoleKeyInfo('\0', ConsoleKey.Delete, false, true, false);
                    Reset();
                    return true;
                }

                if (key.KeyChar is '\x7f' or '\b' || key.Key == ConsoleKey.Backspace) {
                    decodedKey = new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, true, false);
                    Reset();
                    return true;
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
                    case 'b' or 'B':
                        decodedKey = new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, true, false);
                        Reset();
                        return true;
                    case 'f' or 'F':
                        decodedKey = new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, true, false);
                        Reset();
                        return true;
                    case 'D' or 'd':
                        decodedKey = new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, true, false);
                        Reset();
                        return true;
                    case 'C' or 'c':
                        decodedKey = new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, true, false);
                        Reset();
                        return true;
                }

                if (AppendDigit(key.KeyChar)) {
                    _state = State.CsiParam;
                    return true;
                }

                Reset();
                replayEscape = true;
                return false;

            case State.CsiParam:
                if (AppendDigit(key.KeyChar)) return true;
                switch (key.KeyChar) {
                    case ';':
                        _param1 = _value;
                        _value = 0;
                        return true;
                    case 'D' or 'd':
                        decodedKey = new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, true, false);
                        Reset();
                        return true;
                    case 'C' or 'c':
                        decodedKey = new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, true, false);
                        Reset();
                        return true;
                    case '~' when (_param1 == 3 || _value == 3):
                        decodedKey = new ConsoleKeyInfo('\0', ConsoleKey.Delete, false, true, false);
                        Reset();
                        return true;
                    default:
                        Reset();
                        return true;
                }

            case State.Button:
                if (AppendDigit(key.KeyChar)) return true;
                if (key.KeyChar == ';') {
                    _button = _value;
                    _value = 0;
                    _state = State.Column;
                    return true;
                }

                Reset();
                return true;

            case State.Column:
                if (AppendDigit(key.KeyChar)) return true;
                if (key.KeyChar == ';') {
                    _column = _value;
                    _value = 0;
                    _state = State.Row;
                    return true;
                }

                Reset();
                return true;

            case State.Row:
                if (AppendDigit(key.KeyChar)) return true;
                if (key.KeyChar is 'M' or 'm') {
                    var col = _column;
                    var row = _value;
                    var btn = _button;
                    if ((btn & 64) != 0) {
                        var kind = (btn & 1) == 0 ? MouseEventKind.WheelUp : MouseEventKind.WheelDown;
                        mouseEvent = new TerminalMouseEvent(kind, col, row);
                    } else if (key.KeyChar == 'M') {
                        if ((btn & 32) != 0) {
                            mouseEvent = new TerminalMouseEvent(MouseEventKind.Drag, col, row);
                        } else if ((btn & 3) == 0) {
                            mouseEvent = new TerminalMouseEvent(MouseEventKind.Down, col, row);
                        }
                    } else if (key.KeyChar == 'm') {
                        if ((btn & 3) == 0) {
                            mouseEvent = new TerminalMouseEvent(MouseEventKind.Up, col, row);
                        }
                    }
                }

                Reset();
                return true;

            default: throw new InvalidOperationException("Unknown terminal input state");
        }
    }

    public bool Flush(out bool escaped) {
        escaped = false;
        if (_state == State.None || Stopwatch.GetElapsedTime(_escapeTimestamp).TotalMilliseconds < EscapeTimeoutMs) {
            return false;
        }

        escaped = _state == State.Escape;
        Reset();
        return escaped;
    }

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
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

    public async Task<string?> ReadAsync(
        CancellationToken cancellationToken,
        bool masked = false,
        Action? onChanged = null,
        Func<ConsoleKeyInfo, bool>? onSpecialKey = null,
        Action<TerminalMouseEvent>? onMouseEvent = null,
        bool recordHistory = true) {
        Reset();
        onChanged?.Invoke();
        var inputParser = new TerminalInputParser();

        while (!cancellationToken.IsCancellationRequested) {
            if (!Console.KeyAvailable) {
                if (inputParser.Flush(out var escaped) && escaped) {
                    var handled = onSpecialKey?.Invoke(
                        new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false)) == true;
                    if (!handled) {
                        lock (_gate) {
                            _text = string.Empty;
                            _caret = 0;
                        }
                    }

                    onChanged?.Invoke();
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
                var handled = onSpecialKey?.Invoke(
                    new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false)) == true;
                if (!handled) {
                    lock (_gate) {
                        _text = string.Empty;
                        _caret = 0;
                    }
                }
            }

            if (mouseEvent.Kind != MouseEventKind.None) {
                onMouseEvent?.Invoke(mouseEvent);
            }

            if (consumed || onSpecialKey?.Invoke(key) == true) {
                onChanged?.Invoke();
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

            onChanged?.Invoke();
            if (!completed) continue;

            Reset();
            onChanged?.Invoke();
            return result;
        }

        Reset();
        onChanged?.Invoke();
        return null;
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

/// <summary>Decodes terminal input sequences.</summary>
internal sealed class TerminalInputParser {
    private enum State {
        None,
        Escape,
        Csi,
        Button,
        Column,
        Row
    }

    private State _state;
    private int _button;
    private int _column;
    private int _value;

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
                return true;

            case State.Escape:
                if (key.KeyChar == '[') {
                    _state = State.Csi;
                    return true;
                }

                if (key.Key == ConsoleKey.Enter) {
                    decodedKey = new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, true, false);
                    Reset();
                    return true;
                }

                Reset();
                replayEscape = true;
                return false;

            case State.Csi:
                if (key.KeyChar == '<') {
                    _state = State.Button;
                    _value = 0;
                    return true;
                }

                Reset();
                replayEscape = true;
                return false;

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
        escaped = _state == State.Escape;
        if (_state == State.None) return false;
        Reset();
        return escaped;
    }

    private bool AppendDigit(char value) {
        if (value is < '0' or > '9') return false;

        _value = Math.Min(1000, _value * 10 + value - '0');
        return true;
    }

    private void Reset() {
        _state = State.None;
        _button = 0;
        _column = 0;
        _value = 0;
    }
}
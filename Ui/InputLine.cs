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
        Action<int>? onMouseWheel = null,
        bool recordHistory = true) {
        Reset();
        onChanged?.Invoke();
        var mouse = new MouseWheelParser();

        while (!cancellationToken.IsCancellationRequested) {
            if (!Console.KeyAvailable) {
                if (mouse.Flush(out var escaped) && escaped) {
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
            var consumed = mouse.Consume(key, out var wheelDirection, out var replayEscape);
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

            if (wheelDirection != 0) {
                onMouseWheel?.Invoke(wheelDirection);
            }

            if (consumed || onSpecialKey?.Invoke(key) == true) {
                onChanged?.Invoke();
                continue;
            }

            string? result = null;
            var completed = false;
            lock (_gate) {
                switch (key.Key) {
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
                        _caret--;
                        break;

                    case ConsoleKey.Delete when _caret < _text.Length:
                        _text = _text.Remove(_caret, 1);
                        break;

                    case ConsoleKey.LeftArrow when _caret > 0:
                        _caret--;
                        break;

                    case ConsoleKey.RightArrow when _caret < _text.Length:
                        _caret++;
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

    public string Display(bool masked) {
        lock (_gate) {
            var text = masked ? new string('•', _text.Length) : _text;
            return $"┃ {text}";
        }
    }

    public int CursorColumn(bool masked) {
        lock (_gate) {
            var visible = masked ? new string('•', _caret) : _text[.._caret];
            return 1 + CellTextLayout.CellWidth($"┃ {visible}");
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
            var display = masked ? new string('•', candidate.Length) : candidate;
            if (CellTextLayout.CellWidth($"┃ {display}") > Math.Max(1, Console.WindowWidth - 2)) {
                return;
            }

            _text = candidate;
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

/// <summary>Decodes the SGR mouse wheel report without stealing normal keys.</summary>
internal sealed class MouseWheelParser {
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
    private int _value;

    public bool Consume(
        ConsoleKeyInfo key,
        out int wheelDirection,
        out bool replayEscape) {
        wheelDirection = 0;
        replayEscape = false;

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
                    _value = 0;
                    _state = State.Row;
                    return true;
                }

                Reset();
                return true;

            case State.Row:
                if (AppendDigit(key.KeyChar)) return true;
                if (key.KeyChar is 'M' or 'm') {
                    if (key.KeyChar == 'M' && (_button is 64 or 65)) {
                        wheelDirection = _button == 64 ? 1 : -1;
                    }
                }

                Reset();
                return true;

            default: throw new InvalidOperationException("Unknown mouse input state");
        }
    }

    public bool Flush(out bool escaped) {
        escaped = _state == State.Escape;
        if (!escaped) return false;

        Reset();
        return true;
    }

    private bool AppendDigit(char value) {
        if (value is < '0' or > '9') return false;

        _value = Math.Min(1000, _value * 10 + value - '0');
        return true;
    }

    private void Reset() {
        _state = State.None;
        _button = 0;
        _value = 0;
    }
}
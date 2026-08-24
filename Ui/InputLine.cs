using Spectre.Console;
using Spectre.Console.Rendering;

namespace Kite.Ui;

/// <summary>
/// Single-line input editor (fx keybinding subset): ↑↓ history, ←→Home/End cursor,
/// Backspace/Delete, Esc clears, Ctrl+A/E line start/end, Ctrl+C cancels.
/// </summary>
public sealed class InputLine(bool newlineOnEnter = true, Action<int>? onPageScroll = null) {
    private const string Prefix = "┃ ";

    private readonly List<string> _history = [];
    private string _historySnapshot = string.Empty;
    private int _historyIndex;

    /// <summary>
    /// Read one line; null means cancel (Ctrl+C). The input row is also the
    /// message card: it keeps the same look after submit.
    /// masked: render as • and skip history, so ↑ cannot reveal secrets.
    /// </summary>
    public async Task<string?> ReadAsync(CancellationToken cancellationToken, bool masked = false) {
        var text = string.Empty;
        var caret = 0;

        Render(text, caret, masked);

        while (!cancellationToken.IsCancellationRequested) {
            if (!Console.KeyAvailable) {
                await Task.Delay(30, cancellationToken);
                continue;
            }

            var key = Console.ReadKey(intercept: true);

            switch (key.Key) {
                case ConsoleKey.Enter:
                    // Anchored mode: Enter does not add a newline (the view commits the card and resets the row)
                    if (newlineOnEnter) {
                        Console.WriteLine();
                    }

                    if (text.Length > 0 && !masked) {
                        _history.Add(text);
                    }

                    return text;

                case ConsoleKey.Escape:
                    text = string.Empty;
                    caret = 0;
                    break;

                case ConsoleKey.Backspace:
                    // No when-guard: a failing guard falls through to default and
                    // inserts the DEL control char (backspace arrives as 0x7F)
                    if (caret > 0) {
                        text = text.Remove(caret - 1, 1);
                        caret--;
                    }

                    break;

                case ConsoleKey.Delete:
                    if (caret < text.Length) {
                        text = text.Remove(caret, 1);
                    }

                    break;

                case ConsoleKey.LeftArrow when caret > 0:
                    caret--;
                    break;

                case ConsoleKey.RightArrow when caret < text.Length:
                    caret++;
                    break;

                case ConsoleKey.Home:
                case ConsoleKey.A when (key.Modifiers & ConsoleModifiers.Control) != 0:
                    caret = 0;
                    break;

                case ConsoleKey.End:
                case ConsoleKey.E when (key.Modifiers & ConsoleModifiers.Control) != 0:
                    caret = text.Length;
                    break;

                case ConsoleKey.UpArrow:
                    if (_history.Count == 0) {
                        break;
                    }

                    if (_historyIndex == _history.Count) {
                        _historySnapshot = text;
                    }

                    if (_historyIndex > 0) {
                        _historyIndex--;
                        text = _history[_historyIndex];
                        caret = text.Length;
                    }

                    break;

                case ConsoleKey.DownArrow:
                    if (_historyIndex < _history.Count) {
                        _historyIndex++;
                        text = _historyIndex == _history.Count ? _historySnapshot : _history[_historyIndex];
                        caret = text.Length;
                    }

                    break;

                case ConsoleKey.PageUp:
                    onPageScroll?.Invoke(1);
                    Render(text, caret, masked);
                    break;

                case ConsoleKey.PageDown:
                    onPageScroll?.Invoke(-1);
                    Render(text, caret, masked);
                    break;

                case ConsoleKey.C when (key.Modifiers & ConsoleModifiers.Control) != 0:
                    Console.WriteLine();
                    return null;

                default:
                    // Printable chars only: 0x7F (DEL) and other C1 controls are
                    // keyboard actions already handled above, never text
                    if ((key.KeyChar >= ' ' && key.KeyChar != '\x7f') || key.Key == ConsoleKey.Spacebar) {
                        var candidate = text.Insert(caret, key.KeyChar.ToString());
                        // Reject input that would exceed the display width,
                        // keeping the input row one physical line
                        if (new Segment(Prefix + candidate, Style.Plain).CellCount() <= MaxInputCells) {
                            text = candidate;
                            caret++;
                        }
                    }

                    break;
            }

            Render(text, caret, masked);
        }

        return null;
    }

    private static int MaxInputCells => Math.Max(10, Math.Max(1, Console.WindowWidth) - 2);

    private static void Render(string text, int caret, bool masked = false) {
        // Full-row repaint: home (CH1) + erase line (EL2) + rewrite + absolute CHA caret
        // masked: one • per character (CJK becomes one cell too, so caret indexes line up)
        var display = masked ? new string('•', text.Length) : text;
        var caretCol = 1 + new Segment(Prefix + display[..caret], Style.Plain).CellCount();
        Console.Write($"\e[1G\e[2K{Prefix}{display}\e[{caretCol}G");
    }
}
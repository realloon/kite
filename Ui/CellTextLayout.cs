using Spectre.Console;
using Spectre.Console.Rendering;
using System.Text;

namespace Kite.Ui;

/// <summary>
/// Incremental hard-wrapped text. Layout work is proportional to appended text;
/// a resize rebuilds the entry once. It deliberately does not parse Markdown.
/// </summary>
internal sealed class CellTextLayout {
    private readonly StringBuilder _text = new();
    private readonly List<string> _completedLines = [];
    private readonly StringBuilder _currentLine = new();
    private int _width = 1;
    private int _currentWidth;

    public int Length => _text.Length;

    public int LineCount => _completedLines.Count + 1;

    public string GetLine(int index) {
        if (index < 0 || index >= LineCount) {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return index < _completedLines.Count ? _completedLines[index] : _currentLine.ToString();
    }

    public void SetWidth(int width) {
        width = Math.Max(1, width);
        if (width == _width) return;

        _width = width;
        _completedLines.Clear();
        _currentLine.Clear();
        _currentWidth = 0;
        Process(_text.ToString());
    }

    public void Append(string text) {
        if (text.Length == 0) return;

        _text.Append(text);
        Process(text);
    }

    public static int CellWidth(string text) => new Segment(text, Style.Plain).CellCount();

    private static int CellWidth(Rune rune) => CellWidth(rune.ToString());

    public static string Clip(string text, int width) {
        if (CellWidth(text) <= width) {
            return text;
        }

        var result = new StringBuilder();
        var cells = 0;
        foreach (var rune in text.EnumerateRunes()) {
            var runeWidth = CellWidth(rune);
            if (cells + runeWidth > width) break;

            result.Append(rune);
            cells += runeWidth;
        }

        return result.ToString();
    }

    private void Process(string text) {
        foreach (var rune in text.EnumerateRunes()) {
            switch (rune.Value) {
                case '\r':
                    continue;
                case '\n':
                    FlushLine();
                    continue;
                case '\t':
                    AppendRune(new Rune(' '));
                    AppendRune(new Rune(' '));
                    AppendRune(new Rune(' '));
                    AppendRune(new Rune(' '));
                    continue;
                // Model output is text, not terminal control input.
                case < 0x20 or >= 0x7f and <= 0x9f:
                    continue;
                default:
                    AppendRune(rune);
                    break;
            }
        }
    }

    private void AppendRune(Rune rune) {
        var runeWidth = CellWidth(rune);
        if (_currentLine.Length > 0 && _currentWidth + runeWidth > _width) {
            FlushLine();
        }

        _currentLine.Append(rune);
        _currentWidth += runeWidth;
    }

    private void FlushLine() {
        _completedLines.Add(_currentLine.ToString());
        _currentLine.Clear();
        _currentWidth = 0;
    }
}
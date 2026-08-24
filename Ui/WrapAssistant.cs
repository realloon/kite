using Spectre.Console;
using Spectre.Console.Rendering;
using System.Text;

namespace Kite.Ui;

/// <summary>
/// fx-style wrapping: word breaks, orphan avoidance, CJK/emoji width awareness
/// (width measured with Spectre's Segment.CellCount()).
/// </summary>
public static class WrapAssistant {
    /// <summary>Wrap a paragraph into lines with a gutter prefix. Hard newlines are kept as blank separators.</summary>
    public static List<string> Wrap(string text, int contentCols, string gutter) {
        var lines = new List<string>();

        foreach (var paragraph in text.Split('\n')) {
            if (paragraph.Length == 0) {
                lines.Add(string.Empty);
                continue;
            }

            WrapParagraph(paragraph, contentCols, gutter, lines);
        }

        return lines;
    }

    private static void WrapParagraph(string paragraph, int cols, string gutter, List<string> lines) {
        var current = new List<string>();
        var currentWidth = 0;
        var words = SplitWords(paragraph);

        foreach (var word in words) {
            var wordWidth = new Segment(word, Style.Plain).CellCount();

            if (currentWidth + wordWidth > cols && current.Count > 0) {
                // Line full: if the next line would hold one orphan word, let it overflow this line instead
                if (current.Count == 1 && currentWidth + wordWidth <= cols) {
                    current.Add(word);
                    currentWidth += wordWidth;
                    continue;
                }

                lines.Add(gutter + string.Join(' ', current));
                current.Clear();
                currentWidth = 0;
            }

            // Word wider than the whole line: split it by rune
            if (wordWidth > cols) {
                FlushCurrent(current, lines, gutter);
                current.Clear();
                currentWidth = 0;
                foreach (var piece in BreakByCells(word, cols)) {
                    lines.Add(gutter + piece);
                }

                continue;
            }

            current.Add(word);
            currentWidth += wordWidth + (current.Count > 1 ? 1 : 0);
        }

        FlushCurrent(current, lines, gutter);
    }

    private static void FlushCurrent(List<string> current, List<string> lines, string gutter) {
        if (current.Count == 0) return;

        lines.Add(gutter + string.Join(' ', current));
    }

    /// <summary>Split on whitespace; a long run without spaces (e.g. CJK) stays one token.</summary>
    private static List<string> SplitWords(string text) {
        var words = new List<string>();
        var sb = new StringBuilder();

        foreach (var c in text) {
            if (char.IsWhiteSpace(c)) {
                if (sb.Length > 0) {
                    words.Add(sb.ToString());
                    sb.Clear();
                }
            } else {
                sb.Append(c);
            }
        }

        if (sb.Length > 0) {
            words.Add(sb.ToString());
        }

        return words;
    }

    /// <summary>Split an over-wide word into pieces by display width (CJK counts 2 cells).</summary>
    private static List<string> BreakByCells(string word, int cols) {
        var pieces = new List<string>();
        var sb = new StringBuilder();
        var width = 0;

        foreach (var rune in word.EnumerateRunes()) {
            var cellWidth = new Segment(rune.ToString(), Style.Plain).CellCount();
            if (width + cellWidth > cols && sb.Length > 0) {
                pieces.Add(sb.ToString());
                sb.Clear();
                width = 0;
            }

            sb.Append(rune);
            width += cellWidth;
        }

        if (sb.Length > 0) {
            pieces.Add(sb.ToString());
        }

        return pieces;
    }
}
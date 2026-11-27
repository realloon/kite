using System.Text;

namespace Kite.Ui;

internal static class AnsiText {
    public static (int FromRow, int FromCol, int ToRow, int ToCol) NormalizeSelection(
        (int Col, int Row) start,
        (int Col, int Row) end) =>
        start.Row < end.Row || (start.Row == end.Row && start.Col <= end.Col)
            ? (start.Row, start.Col, end.Row, end.Col)
            : (end.Row, end.Col, start.Row, start.Col);

    public static string Strip(string text) {
        if (!text.Contains('\e')) {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length;) {
            if (text[i] == '\e') {
                i = SkipEscapeSequence(text, i);
                continue;
            }

            sb.Append(text[i]);
            i += 1;
        }

        return sb.ToString();
    }

    public static string ExtractCellRange(string line, int startCol, int endCol) {
        if (startCol >= endCol || string.IsNullOrEmpty(line)) {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var currentCell = 0;
        foreach (var rune in line.EnumerateRunes()) {
            var width = CellTextLayout.CellWidth(rune);
            if (width == 0) continue;

            var cellStart = currentCell;
            var cellEnd = currentCell + width;
            currentCell += width;

            if (cellStart < endCol && cellEnd > startCol) {
                sb.Append(rune);
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static string HighlightCellRange(string ansiLine, int startCol, int endCol) {
        if (startCol >= endCol || string.IsNullOrEmpty(ansiLine)) {
            return ansiLine;
        }

        var sb = new StringBuilder(ansiLine.Length + 16);
        var currentCell = 0;
        var inHighlight = false;

        for (var i = 0; i < ansiLine.Length;) {
            if (ansiLine[i] == '\e') {
                var next = SkipEscapeSequence(ansiLine, i);
                var seq = ansiLine[i..next];
                i = next;
                sb.Append(seq);
                if (inHighlight && seq is "\e[0m" or "\e[m") {
                    sb.Append("\e[7m");
                }

                continue;
            }

            var rune = Rune.GetRuneAt(ansiLine, i);
            i += rune.Utf16SequenceLength;
            var width = CellTextLayout.CellWidth(rune);
            if (width == 0) {
                sb.Append(rune);
                continue;
            }

            var cellStart = currentCell;
            var cellEnd = currentCell + width;
            currentCell += width;

            var shouldHighlight = cellStart < endCol && cellEnd > startCol;
            if (shouldHighlight && !inHighlight) {
                sb.Append("\e[7m");
                inHighlight = true;
            } else if (!shouldHighlight && inHighlight) {
                sb.Append("\e[27m");
                inHighlight = false;
            }

            sb.Append(rune);
        }

        if (inHighlight) {
            sb.Append("\e[27m");
        }

        return sb.ToString();
    }

    private static int SkipEscapeSequence(ReadOnlySpan<char> text, int index) {
        index += 1;
        if (index >= text.Length || text[index] != '[') {
            return index;
        }

        index += 1;
        while (index < text.Length && text[index] is (>= '0' and <= '9') or ';' or '?' or '<') {
            index += 1;
        }

        if (index < text.Length) {
            index += 1;
        }

        return index;
    }
}
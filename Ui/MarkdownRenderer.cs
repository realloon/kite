using System.Text;

namespace Kite.Ui;

internal static class MarkdownRenderer {
    private const string BoldOpen = "\e[1m";
    private const string BoldClose = "\e[22m";
    private const string DimBullet = "\e[2m• \e[22m";

    public static string Render(string text) {
        var result = new StringBuilder(text.Length);
        var lineStart = true;
        var bold = false;

        for (var index = 0; index < text.Length;) {
            if (lineStart && TryReadListMarker(text, index, out var markerEnd)) {
                result.Append(text.AsSpan(index, markerEnd - index - 2));
                result.Append(DimBullet);
                index = markerEnd;
                lineStart = false;
                continue;
            }

            if (TryReadStrongMarker(text, index, bold, out var markerLength)) {
                result.Append(bold ? BoldClose : BoldOpen);
                bold = !bold;
                index += markerLength;
                lineStart = false;
                continue;
            }

            var value = text[index];
            index += 1;
            result.Append(value);
            lineStart = value == '\n';
        }

        if (bold) {
            result.Append(BoldClose);
        }

        return result.ToString();
    }

    private static bool TryReadListMarker(string text, int start, out int end) {
        var marker = start;
        while (marker < text.Length && text[marker] == ' ') {
            marker += 1;
        }

        if (marker + 1 >= text.Length || text[marker] is not ('-' or '*') || text[marker + 1] != ' ') {
            end = 0;
            return false;
        }

        end = marker + 2;
        return true;
    }

    private static bool TryReadStrongMarker(
        string text,
        int start,
        bool bold,
        out int length) {
        if (start + 1 >= text.Length || text[start] is not ('*' or '_') || text[start] != text[start + 1]) {
            length = 0;
            return false;
        }

        if (bold) {
            if (start == 0 || char.IsWhiteSpace(text[start - 1])) {
                length = 0;
                return false;
            }
        } else if (start + 2 >= text.Length
                   || char.IsWhiteSpace(text[start + 2])
                   || !text.AsSpan(start + 2).Contains(text.AsSpan(start, 2), StringComparison.Ordinal)) {
            length = 0;
            return false;
        }

        length = 2;
        return true;
    }
}
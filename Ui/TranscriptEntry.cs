namespace Kite.Ui;

public enum TranscriptEntryKind {
    User,
    Assistant,
    Reasoning,
    Tool,
    Info,
    Error
}

public sealed record TranscriptItem(
    TranscriptEntryKind Kind,
    string Text,
    bool IsStreaming = false,
    bool Expanded = false);

internal sealed class TranscriptEntry(TranscriptEntryKind kind, bool expanded = true) {
    public TranscriptEntryKind Kind { get; } = kind;

    public CellTextLayout Content { get; } = new();

    public bool Expanded { get; set; } = expanded;

    public bool IsStreaming { get; set; }

    public int DisplayLineCount {
        get {
            if (Kind == TranscriptEntryKind.Reasoning) {
                return Expanded && Content.Length > 0 ? Content.LineCount + 1 : 1;
            }

            return Content.Length > 0 || IsStreaming ? Content.LineCount : 1;
        }
    }

    public void Append(string text) => Content.Append(text);

    public void SetScreenWidth(int screenWidth) {
        const int prefixCells = 2;
        Content.SetWidth(Math.Max(1, screenWidth - prefixCells));
    }

    public string DisplayLine(int index) {
        if (Kind != TranscriptEntryKind.Reasoning) {
            if (Content.Length == 0) return Prefix;

            var line = Content.GetLine(index);
            return Kind == TranscriptEntryKind.Assistant
                ? $"{Prefix}{MarkdownRenderer.Render(line)}"
                : $"{Prefix}{line}";
        }

        var label = IsStreaming ? "Thinking" : "Thought";
        if (!Expanded || Content.Length == 0) {
            return $"• {label}";
        }

        return index == 0 ? $"• {label}" : $"  {Content.GetLine(index - 1)}";
    }

    private string Prefix => Kind switch {
        TranscriptEntryKind.User => "┃ ",
        TranscriptEntryKind.Tool => "",
        TranscriptEntryKind.Error => "! ",
        _ => "  "
    };
}
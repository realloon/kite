namespace Kite.Ui;

internal enum TranscriptEntryKind {
    User,
    Assistant,
    Reasoning,
    Tool,
    Info,
    Error
}

internal sealed class TranscriptEntry(int id, TranscriptEntryKind kind, bool expanded = true) {
    public int Id { get; } = id;

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
        var prefixCells = Kind == TranscriptEntryKind.Reasoning ? 4 : 2;
        Content.SetWidth(Math.Max(1, screenWidth - prefixCells));
    }

    public string DisplayLine(int index) {
        if (Kind == TranscriptEntryKind.Reasoning) {
            var label = IsStreaming ? "Thinking" : "Thought";
            if (!Expanded || Content.Length == 0) {
                return $"  ▸ {label}";
            }

            return index == 0 ? $"  ▾ {label}" : $"    {Content.GetLine(index - 1)}";
        }

        if (Content.Length == 0) {
            return Prefix;
        }

        return $"{Prefix}{Content.GetLine(index)}";
    }

    private string Prefix => Kind switch {
        TranscriptEntryKind.User => "┃ ",
        TranscriptEntryKind.Tool => "· ",
        TranscriptEntryKind.Error => "! ",
        _ => "  "
    };
}

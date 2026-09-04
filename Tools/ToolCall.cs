using System.Text.Json;

namespace Kite.Tools;

public sealed record ToolCall(string Id, string Name, string Arguments) {
    private const int PreviewLength = 120;

    public string Preview => FormatPreview(Name, Arguments);

    public static string FormatPreview(string name, string arguments) {
        var preview = arguments
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');
        if (preview.Length > PreviewLength) {
            preview = $"{preview[..(PreviewLength - 1)]}…";
        }

        return preview.Length == 0
            ? $"• {name}"
            : $"• {name} {preview}";
    }
}

public sealed record ToolDefinition(string Name, string Description, JsonElement Parameters) {
    public string Type { get; } = "function";
}
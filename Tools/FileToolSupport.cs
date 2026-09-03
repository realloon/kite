using System.Text;
using System.Text.Json;

namespace Kite.Tools;

internal static class FileToolSupport {
    public const int MaxReadLines = 2_000;
    public const int MaxReadLineLength = 2_000;
    public const int MaxReadBytes = 50 * 1024;
    public const int MaxWriteBytes = 4 * 1024 * 1024;

    public static readonly Lock MutationGate = new();

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static JsonDocument ParseObject(string arguments, string toolName) {
        JsonDocument document;
        try {
            document = JsonDocument.Parse(arguments);
        } catch (JsonException ex) {
            throw new InvalidOperationException($"{toolName} arguments are invalid JSON", ex);
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object) {
            document.Dispose();
            throw new InvalidOperationException($"{toolName} arguments must be an object");
        }

        return document;
    }

    public static void RejectUnknownProperties(JsonElement root, string toolName, params string[] allowed) {
        foreach (var property in root.EnumerateObject()) {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) {
                throw new InvalidOperationException($"{toolName} does not support argument '{property.Name}'");
            }
        }
    }

    public static string RequiredString(JsonElement root, string toolName, string name) {
        if (!root.TryGetProperty(name, out var value)) {
            throw new InvalidOperationException($"{toolName} requires string field '{name}'");
        }

        if (value.ValueKind != JsonValueKind.String) {
            throw new InvalidOperationException($"{toolName} field '{name}' must be a string");
        }

        return value.GetString() ?? throw new InvalidOperationException($"{toolName} field '{name}' is null");
    }

    public static int OptionalPositiveInteger(
        JsonElement root,
        string toolName,
        string name,
        int defaultValue,
        int? maximum = null) {
        if (!root.TryGetProperty(name, out var value)) return defaultValue;

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result) || result < 1) {
            throw new InvalidOperationException($"{toolName} field '{name}' must be a positive integer");
        }

        if (maximum is not null && result > maximum) {
            throw new InvalidOperationException($"{toolName} field '{name}' must be at most {maximum}");
        }

        return result;
    }

    public static string ResolvePath(string path, string workspace, string toolName) {
        if (string.IsNullOrWhiteSpace(path)) {
            throw new InvalidOperationException($"{toolName} path must not be empty");
        }

        var root = Path.GetFullPath(workspace);
        var fullPath = Path.GetFullPath(
            Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        var relative = Path.GetRelativePath(root, fullPath);
        var outside = relative == ".."
                      || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                      || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
                      || Path.IsPathRooted(relative);
        if (outside) {
            throw new InvalidOperationException($"{toolName} path must stay inside the workspace: {path}");
        }

        return fullPath;
    }

    public static string DisplayPath(string path, string workspace) {
        var relative = Path.GetRelativePath(Path.GetFullPath(workspace), path);
        return relative == "." ? Path.GetFileName(path) : relative;
    }

    public static TextFile ReadText(string path) {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = StrictUtf8.GetString(bytes.AsSpan(bom ? 3 : 0));
        foreach (var character in text) {
            if (character < 9 || character > 13 && character < 32) {
                throw new InvalidOperationException($"file is binary: {path}");
            }
        }

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return new TextFile(text, bom, newline);
    }

    public static void EnsureTextFile(string path, string toolName) {
        using var stream = File.OpenRead(path);
        Span<byte> buffer = stackalloc byte[8192];
        var count = stream.Read(buffer);
        if (buffer[..count].Contains((byte)0)) {
            throw new InvalidOperationException($"{toolName} cannot read binary file: {path}");
        }
    }

    public static bool HasUtf8Bom(string path) {
        using var stream = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[3];
        return stream.Read(prefix) == 3 &&
               prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
    }

    public static void WriteTextAtomic(string path, string content, bool preserveBom) {
        ValidateTextSize(content);

        var output = preserveBom && !content.StartsWith('\uFEFF')
            ? $"\uFEFF{content}"
            : content;
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporaryPath, output, StrictUtf8);
            File.Move(temporaryPath, path, overwrite: true);
        } finally {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static void ValidateTextSize(string content) {
        if (StrictUtf8.GetByteCount(content) > MaxWriteBytes) {
            throw new InvalidOperationException($"content exceeds the {MaxWriteBytes / 1024 / 1024} MiB limit");
        }
    }

    public static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    public sealed record TextFile(string Text, bool Bom, string Newline);
}

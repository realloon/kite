using System.Text;
using System.Text.Json;

namespace Kite.Tools;

internal static class FileTools {
    private const string ReadName = "read";
    private const string WriteName = "write";
    private const string PatchName = "patch";

    private const int MaxReadLines = 2_000;
    private const int MaxReadLineLength = 2_000;
    private const int MaxReadBytes = 50 * 1024;
    private const int MaxWriteBytes = 4 * 1024 * 1024;

    private static readonly Lock MutationGate = new();
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static IReadOnlyList<ToolDefinition> Definitions { get; } = [
        new(
            ReadName,
            "Read a file.",
            Schema("""
                   {
                     "type": "object",
                     "properties": {
                       "path": { "type": "string", "description": "File path." },
                       "offset": { "type": "integer", "minimum": 1, "description": "1-based first line to return. Defaults to 1." },
                       "limit": { "type": "integer", "minimum": 1, "maximum": 2000, "description": "Maximum number of lines to return. Defaults to 2000." }
                     },
                     "required": ["path"],
                     "additionalProperties": false
                   }
                   """)),
        new(
            WriteName,
            "Write a file.",
            Schema("""
                   {
                     "type": "object",
                     "properties": {
                       "path": { "type": "string", "description": "File path." },
                       "content": { "type": "string", "description": "The complete file content." }
                     },
                     "required": ["path", "content"],
                     "additionalProperties": false
                   }
                   """)),
        new(
            PatchName,
            "Apply an exact patch.",
            Schema("""
                   {
                     "type": "object",
                     "properties": {
                       "patchText": { "type": "string", "description": "A patch containing add, update, or delete file operations." }
                     },
                     "required": ["patchText"],
                     "additionalProperties": false
                   }
                   """))
    ];

    public static string Execute(
        ToolCall call,
        string workspace,
        CancellationToken cancellationToken) => call.Name switch {
        ReadName => Read(call.Arguments, workspace, cancellationToken),
        WriteName => Write(call.Arguments, workspace, cancellationToken),
        PatchName => ApplyPatch(call.Arguments, workspace, cancellationToken),
        _ => throw new InvalidOperationException($"Unknown tool: {call.Name}")
    };

    private static string Read(string arguments, string workspace, CancellationToken cancellationToken) {
        using var document = ParseObject(arguments, ReadName);
        var root = document.RootElement;
        RejectUnknownProperties(root, ReadName, "path", "offset", "limit");
        var pathText = RequiredString(root, ReadName, "path");
        var offset = OptionalPositiveInteger(root, ReadName, "offset", 1);
        var limit = OptionalPositiveInteger(root, ReadName, "limit", MaxReadLines, MaxReadLines);
        var path = ResolvePath(pathText, workspace, ReadName);
        EnsureRegularFile(path, ReadName);
        cancellationToken.ThrowIfCancellationRequested();

        var output = new StringBuilder();
        var outputBytes = 0;
        using var lines = File.ReadLines(path, new UTF8Encoding(false, true)).GetEnumerator();
        var lineNumber = 0;
        while (lineNumber < offset - 1) {
            if (!lines.MoveNext()) {
                throw new InvalidOperationException($"offset is beyond the end of file: {offset}");
            }

            if (lines.Current.Contains('\0')) {
                throw new InvalidOperationException($"{ReadName} cannot read binary file: {path}");
            }

            lineNumber += 1;
        }

        var selected = 0;
        var nextOffset = 0;
        while (selected < limit && lines.MoveNext()) {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber += 1;
            if (lines.Current.Contains('\0')) {
                throw new InvalidOperationException($"{ReadName} cannot read binary file: {path}");
            }

            var text = FormatLine(lines.Current);
            var rendered = $"{lineNumber}: {text}";
            var byteCount = Encoding.UTF8.GetByteCount(rendered);
            var separatorBytes = selected == 0 ? 0 : 1;
            if (outputBytes + separatorBytes + byteCount > MaxReadBytes) {
                nextOffset = lineNumber;
                break;
            }

            if (output.Length > 0) {
                output.Append('\n');
            }

            output.Append(rendered);
            outputBytes += separatorBytes + byteCount;
            selected += 1;
        }

        if (nextOffset == 0 && selected == limit && lines.MoveNext()) {
            nextOffset = lineNumber + 1;
        }

        var result = output.Length == 0 ? "[empty file]" : output.ToString();
        return nextOffset == 0
            ? result
            : $"{result}\n[truncated; next offset: {nextOffset}]";
    }

    private static string Write(string arguments, string workspace, CancellationToken cancellationToken) {
        using var document = ParseObject(arguments, WriteName);
        var root = document.RootElement;
        RejectUnknownProperties(root, WriteName, "path", "content");
        var pathText = RequiredString(root, WriteName, "path");
        var content = RequiredString(root, WriteName, "content");
        var path = ResolvePath(pathText, workspace, WriteName);

        lock (MutationGate) {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(path)) {
                throw new InvalidOperationException($"path is a directory: {pathText}");
            }

            var existed = File.Exists(path);
            var bom = existed && HasUtf8Bom(path);
            WriteTextAtomic(path, content, bom);
            return $"{(existed ? "Updated" : "Created")} {DisplayPath(path, workspace)}";
        }
    }

    private static string ApplyPatch(string arguments, string workspace, CancellationToken cancellationToken) {
        using var document = ParseObject(arguments, PatchName);
        var root = document.RootElement;
        RejectUnknownProperties(root, PatchName, "patchText");
        var patchText = RequiredString(root, PatchName, "patchText");
        var operations = ParsePatch(patchText);

        lock (MutationGate) {
            var changes = new List<PendingChange>(operations.Count);
            var paths = new HashSet<string>(PathComparer);
            foreach (var operation in operations) {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ResolvePath(operation.Path, workspace, PatchName);
                if (!paths.Add(path)) {
                    throw new InvalidOperationException(
                        $"patch contains the same path more than once: {operation.Path}");
                }

                switch (operation.Kind) {
                    case PatchKind.Add:
                        if (File.Exists(path) || Directory.Exists(path)) {
                            throw new InvalidOperationException($"cannot add an existing path: {operation.Path}");
                        }

                        ValidateTextSize(operation.Content!);
                        changes.Add(new PendingChange(PatchKind.Add, path, operation.Content!, false));
                        break;
                    case PatchKind.Delete:
                        EnsureRegularFile(path, PatchName);
                        changes.Add(new PendingChange(PatchKind.Delete, path, null, false));
                        break;
                    case PatchKind.Update:
                        EnsureRegularFile(path, PatchName);
                        var source = ReadText(path);
                        var content = ApplyUpdate(source, operation.Hunks!, operation.Path);
                        ValidateTextSize(content);
                        changes.Add(new PendingChange(PatchKind.Update, path, content, source.Bom));
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown patch operation: {operation.Kind}");
                }
            }

            foreach (var change in changes) {
                cancellationToken.ThrowIfCancellationRequested();
                if (change.Kind == PatchKind.Delete) {
                    File.Delete(change.Path);
                } else {
                    WriteTextAtomic(change.Path, change.Content!, change.Bom);
                }
            }

            return changes.Count == 1
                ? $"Applied patch to {DisplayPath(changes[0].Path, workspace)}"
                : $"Applied patch to {changes.Count} files";
        }
    }

    private static List<PatchOperation> ParsePatch(string patchText) {
        var lines = NormalizeLineEndings(patchText).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count < 2 || lines[0] != "*** Begin Patch" || lines[^1] != "*** End Patch") {
            throw new InvalidOperationException("patch must start with '*** Begin Patch' and end with '*** End Patch'");
        }

        var operations = new List<PatchOperation>();
        var end = lines.Count - 1;
        var index = 1;
        while (index < end) {
            var line = lines[index];
            if (line.StartsWith("*** Add File:", StringComparison.Ordinal)) {
                var path = PatchPath(line, "*** Add File:");
                index += 1;
                var content = new List<string>();
                while (index < end && !lines[index].StartsWith("***", StringComparison.Ordinal)) {
                    if (!lines[index].StartsWith('+')) {
                        throw new InvalidOperationException($"invalid add line for {path}: {lines[index]}");
                    }

                    content.Add(lines[index][1..]);
                    index += 1;
                }

                if (content.Count == 0) throw new InvalidOperationException($"add operation has no content: {path}");
                operations.Add(new PatchOperation(PatchKind.Add, path, string.Join('\n', content) + '\n', null));
                continue;
            }

            if (line.StartsWith("*** Delete File:", StringComparison.Ordinal)) {
                operations.Add(new PatchOperation(
                    PatchKind.Delete, PatchPath(line, "*** Delete File:"), null, null));
                index += 1;
                continue;
            }

            if (line.StartsWith("*** Update File:", StringComparison.Ordinal)) {
                var path = PatchPath(line, "*** Update File:");
                index += 1;
                var hunks = new List<PatchHunk>();
                while (index < end && lines[index].StartsWith("@@", StringComparison.Ordinal)) {
                    var context = lines[index].Length == 2 ? null : lines[index][2..].Trim();
                    index += 1;
                    var oldLines = new List<string>();
                    var newLines = new List<string>();
                    var endOfFile = false;
                    while (index < end) {
                        line = lines[index];
                        if (line.StartsWith("@@", StringComparison.Ordinal)) break;
                        if (line == "*** End of File") {
                            endOfFile = true;
                            index += 1;
                            break;
                        }

                        if (line.StartsWith("***", StringComparison.Ordinal)) break;
                        if (line.Length == 0 || line[0] == ' ') {
                            var value = line.Length == 0 ? string.Empty : line[1..];
                            oldLines.Add(value);
                            newLines.Add(value);
                        } else if (line[0] == '-') {
                            oldLines.Add(line[1..]);
                        } else if (line[0] == '+') {
                            newLines.Add(line[1..]);
                        } else {
                            throw new InvalidOperationException($"invalid update line for {path}: {line}");
                        }

                        index += 1;
                    }

                    if (oldLines.Count == 0 && newLines.Count == 0) {
                        throw new InvalidOperationException($"update hunk is empty: {path}");
                    }

                    hunks.Add(new PatchHunk(context, oldLines, newLines, endOfFile));
                }

                if (hunks.Count == 0) throw new InvalidOperationException($"update operation has no hunks: {path}");
                operations.Add(new PatchOperation(PatchKind.Update, path, null, hunks));
                continue;
            }

            throw new InvalidOperationException($"invalid patch line: {line}");
        }

        return operations;
    }

    private static string ApplyUpdate(
        TextFile source,
        IReadOnlyList<PatchHunk> hunks,
        string displayPath) {
        var lines = SplitLines(source.Text);
        var cursor = 0;
        foreach (var hunk in hunks) {
            if (hunk.Context is not null) {
                var contextIndex = FindSequence(lines, [hunk.Context], cursor);
                if (contextIndex < 0) {
                    throw new InvalidOperationException($"failed to find context '{hunk.Context}' in {displayPath}");
                }

                cursor = contextIndex + 1;
            }

            var start = hunk.OldLines.Count == 0
                ? hunk.Context is null ? lines.Count : cursor
                : FindSequence(lines, hunk.OldLines, cursor);
            if (start < 0) {
                throw new InvalidOperationException(
                    $"failed to find expected lines in {displayPath}:\n{string.Join('\n', hunk.OldLines)}");
            }

            if (hunk.EndOfFile && start + hunk.OldLines.Count != lines.Count) {
                throw new InvalidOperationException($"expected the patch hunk to end at the end of {displayPath}");
            }

            lines.RemoveRange(start, hunk.OldLines.Count);
            lines.InsertRange(start, hunk.NewLines);
            cursor = start + hunk.NewLines.Count;
        }

        var content = string.Join('\n', lines);
        if (source.Text.EndsWith('\n') && lines.Count > 0) {
            content += '\n';
        }

        return content.Replace("\n", source.Newline, StringComparison.Ordinal);
    }

    private static List<string> SplitLines(string text) {
        if (text.Length == 0) return [];
        var lines = NormalizeLineEndings(text).Split('\n').ToList();
        if (lines[^1].Length == 0) {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static int FindSequence(
        List<string> lines,
        IReadOnlyList<string> sequence,
        int start) {
        if (sequence.Count == 0) return -1;
        for (var index = Math.Max(0, start); index + sequence.Count <= lines.Count; index++) {
            var matches = true;
            for (var offset = 0; offset < sequence.Count; offset++) {
                if (string.Equals(lines[index + offset], sequence[offset], StringComparison.Ordinal)) continue;

                matches = false;
                break;
            }

            if (matches) return index;
        }

        return -1;
    }

    private static string PatchPath(string line, string marker) {
        var path = line[marker.Length..].Trim();
        return path.Length == 0 ? throw new InvalidOperationException($"patch path is empty after '{marker}'") : path;
    }

    private static string FormatLine(string line) {
        if (line.Length <= MaxReadLineLength) return line;
        var length = MaxReadLineLength;
        if (char.IsHighSurrogate(line[length - 1])) {
            length -= 1;
        }

        return $"{line[..length]}... [line truncated]";
    }

    private static void EnsureRegularFile(string path, string toolName) {
        if (Directory.Exists(path)) {
            throw new InvalidOperationException($"{toolName} path is a directory: {path}");
        }

        if (!File.Exists(path)) {
            throw new FileNotFoundException($"{toolName} file does not exist: {path}");
        }
    }

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private enum PatchKind {
        Add,
        Delete,
        Update
    }

    private sealed record PatchOperation(
        PatchKind Kind,
        string Path,
        string? Content,
        IReadOnlyList<PatchHunk>? Hunks);

    private sealed record PatchHunk(
        string? Context,
        IReadOnlyList<string> OldLines,
        IReadOnlyList<string> NewLines,
        bool EndOfFile);

    private sealed record PendingChange(
        PatchKind Kind,
        string Path,
        string? Content,
        bool Bom);

    private sealed record TextFile(string Text, bool Bom, string Newline);

    private static JsonDocument ParseObject(string arguments, string toolName) {
        JsonDocument document;
        try {
            document = JsonDocument.Parse(arguments);
        } catch (JsonException ex) {
            throw new InvalidOperationException($"{toolName} arguments are invalid JSON", ex);
        }

        if (document.RootElement.ValueKind == JsonValueKind.Object) {
            return document;
        }

        document.Dispose();
        throw new InvalidOperationException($"{toolName} arguments must be an object");
    }

    private static void RejectUnknownProperties(JsonElement root, string toolName, params string[] allowed) {
        foreach (var property in root.EnumerateObject()) {
            if (allowed.Contains(property.Name, StringComparer.Ordinal)) continue;

            throw new InvalidOperationException($"{toolName} does not support argument '{property.Name}'");
        }
    }

    private static string RequiredString(JsonElement root, string toolName, string name) {
        if (!root.TryGetProperty(name, out var value)) {
            throw new InvalidOperationException($"{toolName} requires string field '{name}'");
        }

        if (value.ValueKind != JsonValueKind.String) {
            throw new InvalidOperationException($"{toolName} field '{name}' must be a string");
        }

        return value.GetString() ?? throw new InvalidOperationException($"{toolName} field '{name}' is null");
    }

    private static int OptionalPositiveInteger(
        JsonElement root,
        string toolName,
        string name,
        int defaultValue,
        int? maximum = null) {
        if (!root.TryGetProperty(name, out var value)) return defaultValue;

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result) || result < 1) {
            throw new InvalidOperationException($"{toolName} field '{name}' must be a positive integer");
        }

        return result > maximum
            ? throw new InvalidOperationException($"{toolName} field '{name}' must be at most {maximum}")
            : result;
    }

    private static string ResolvePath(string path, string workingDirectory, string toolName) {
        if (string.IsNullOrWhiteSpace(path)) {
            throw new InvalidOperationException($"{toolName} path must not be empty");
        }

        var root = Path.GetFullPath(workingDirectory);
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
    }

    private static string DisplayPath(string path, string workspace) {
        var relative = Path.GetRelativePath(Path.GetFullPath(workspace), path);
        return relative == "." ? Path.GetFileName(path) : relative;
    }

    private static TextFile ReadText(string path) {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes is [0xEF, 0xBB, 0xBF, ..];
        var text = StrictUtf8.GetString(bytes.AsSpan(bom ? 3 : 0));
        foreach (var character in text) {
            if (character >= 9 && (character <= 13 || character >= 32)) continue;

            throw new InvalidOperationException($"file is binary: {path}");
        }

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return new TextFile(text, bom, newline);
    }

    private static bool HasUtf8Bom(string path) {
        using var stream = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[3];
        return stream.Read(prefix) == 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
    }

    private static void WriteTextAtomic(string path, string content, bool preserveBom) {
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
            if (File.Exists(temporaryPath)) {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ValidateTextSize(string content) {
        if (StrictUtf8.GetByteCount(content) <= MaxWriteBytes) return;

        throw new InvalidOperationException($"content exceeds the {MaxWriteBytes / 1024 / 1024} MiB limit");
    }

    private static string NormalizeLineEndings(string text) => text
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n');
}
using System.Text;
using System.Text.Json;

namespace Kite.Tools;

internal static class FileTools {
    private const string ReadName = "read";
    private const string WriteName = "write";
    private const string PatchName = "patch";

    public static IReadOnlyList<ToolDefinition> Definitions { get; } = [
        new(
            ReadName,
            "Read a UTF-8 text file and return line-numbered content.",
            Schema("""
                   {
                     "type": "object",
                     "properties": {
                       "path": { "type": "string", "description": "File path relative to the current workspace." },
                       "offset": { "type": "integer", "minimum": 1, "description": "1-based first line to return. Defaults to 1." },
                       "limit": { "type": "integer", "minimum": 1, "maximum": 2000, "description": "Maximum number of lines to return. Defaults to 2000." }
                     },
                     "required": ["path"],
                     "additionalProperties": false
                   }
                   """)),
        new(
            WriteName,
            "Create or replace a UTF-8 text file.",
            Schema("""
                   {
                     "type": "object",
                     "properties": {
                       "path": { "type": "string", "description": "File path relative to the current workspace." },
                       "content": { "type": "string", "description": "The complete file content." }
                     },
                     "required": ["path", "content"],
                     "additionalProperties": false
                   }
                   """)),
        new(
            PatchName,
            "Apply an exact multi-file patch to workspace files. Format: *** Begin Patch, then *** Add File: path with + lines, *** Update File: path with @@ hunks using space/-/+ lines, or *** Delete File: path, then *** End Patch. No fuzzy matching.",
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
        using var document = FileToolSupport.ParseObject(arguments, ReadName);
        var root = document.RootElement;
        FileToolSupport.RejectUnknownProperties(root, ReadName, "path", "offset", "limit");
        var pathText = FileToolSupport.RequiredString(root, ReadName, "path");
        var offset = FileToolSupport.OptionalPositiveInteger(root, ReadName, "offset", 1);
        var limit = FileToolSupport.OptionalPositiveInteger(
            root, ReadName, "limit", FileToolSupport.MaxReadLines, FileToolSupport.MaxReadLines);
        var path = FileToolSupport.ResolvePath(pathText, workspace, ReadName);
        EnsureRegularFile(path, ReadName);
        FileToolSupport.EnsureTextFile(path, ReadName);
        cancellationToken.ThrowIfCancellationRequested();

        var output = new StringBuilder();
        var outputBytes = 0;
        using var lines = File.ReadLines(path, new UTF8Encoding(false, true)).GetEnumerator();
        var lineNumber = 0;
        while (lineNumber < offset - 1) {
            if (!lines.MoveNext()) {
                throw new InvalidOperationException($"offset is beyond the end of file: {offset}");
            }

            if (lines.Current.Contains('\0'))
                throw new InvalidOperationException($"{ReadName} cannot read binary file: {path}");
            lineNumber++;
        }

        var selected = 0;
        var nextOffset = 0;
        while (selected < limit && lines.MoveNext()) {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            if (lines.Current.Contains('\0'))
                throw new InvalidOperationException($"{ReadName} cannot read binary file: {path}");
            var text = FormatLine(lines.Current);
            var rendered = $"{lineNumber}: {text}";
            var byteCount = Encoding.UTF8.GetByteCount(rendered);
            var separatorBytes = selected == 0 ? 0 : 1;
            if (outputBytes > 0 && outputBytes + separatorBytes + byteCount > FileToolSupport.MaxReadBytes) {
                nextOffset = lineNumber;
                break;
            }

            if (outputBytes == 0 && byteCount > FileToolSupport.MaxReadBytes) {
                nextOffset = lineNumber;
                break;
            }

            if (output.Length > 0) output.Append('\n');
            output.Append(rendered);
            outputBytes += separatorBytes + byteCount;
            selected++;
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
        using var document = FileToolSupport.ParseObject(arguments, WriteName);
        var root = document.RootElement;
        FileToolSupport.RejectUnknownProperties(root, WriteName, "path", "content");
        var pathText = FileToolSupport.RequiredString(root, WriteName, "path");
        var content = FileToolSupport.RequiredString(root, WriteName, "content");
        var path = FileToolSupport.ResolvePath(pathText, workspace, WriteName);

        lock (FileToolSupport.MutationGate) {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(path)) {
                throw new InvalidOperationException($"path is a directory: {pathText}");
            }

            var existed = File.Exists(path);
            var bom = existed && FileToolSupport.HasUtf8Bom(path);
            FileToolSupport.WriteTextAtomic(path, content, bom);
            return $"{(existed ? "Updated" : "Created")} {FileToolSupport.DisplayPath(path, workspace)}";
        }
    }

    private static string ApplyPatch(string arguments, string workspace, CancellationToken cancellationToken) {
        using var document = FileToolSupport.ParseObject(arguments, PatchName);
        var root = document.RootElement;
        FileToolSupport.RejectUnknownProperties(root, PatchName, "patchText");
        var patchText = FileToolSupport.RequiredString(root, PatchName, "patchText");
        var operations = ParsePatch(patchText);

        lock (FileToolSupport.MutationGate) {
            var changes = new List<PendingChange>(operations.Count);
            var paths = new HashSet<string>(PathComparer);
            foreach (var operation in operations) {
                cancellationToken.ThrowIfCancellationRequested();
                var path = FileToolSupport.ResolvePath(operation.Path, workspace, PatchName);
                if (!paths.Add(path)) {
                    throw new InvalidOperationException(
                        $"patch contains the same path more than once: {operation.Path}");
                }

                switch (operation.Kind) {
                    case PatchKind.Add:
                        if (File.Exists(path) || Directory.Exists(path)) {
                            throw new InvalidOperationException($"cannot add an existing path: {operation.Path}");
                        }

                        FileToolSupport.ValidateTextSize(operation.Content!);
                        changes.Add(new PendingChange(PatchKind.Add, path, operation.Content!, false));
                        break;
                    case PatchKind.Delete:
                        EnsureRegularFile(path, PatchName);
                        changes.Add(new PendingChange(PatchKind.Delete, path, null, false));
                        break;
                    case PatchKind.Update:
                        EnsureRegularFile(path, PatchName);
                        var source = FileToolSupport.ReadText(path);
                        var content = ApplyUpdate(source, operation.Hunks!, operation.Path);
                        FileToolSupport.ValidateTextSize(content);
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
                    FileToolSupport.WriteTextAtomic(change.Path, change.Content!, change.Bom);
                }
            }

            return changes.Count == 1
                ? $"Applied patch to {FileToolSupport.DisplayPath(changes[0].Path, workspace)}"
                : $"Applied patch to {changes.Count} files";
        }
    }

    private static List<PatchOperation> ParsePatch(string patchText) {
        var lines = FileToolSupport.NormalizeLineEndings(patchText).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
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
                index++;
                var content = new List<string>();
                while (index < end && !lines[index].StartsWith("***", StringComparison.Ordinal)) {
                    if (!lines[index].StartsWith('+')) {
                        throw new InvalidOperationException($"invalid add line for {path}: {lines[index]}");
                    }

                    content.Add(lines[index][1..]);
                    index++;
                }

                if (content.Count == 0) throw new InvalidOperationException($"add operation has no content: {path}");
                operations.Add(new PatchOperation(PatchKind.Add, path, string.Join('\n', content) + '\n', null));
                continue;
            }

            if (line.StartsWith("*** Delete File:", StringComparison.Ordinal)) {
                operations.Add(new PatchOperation(
                    PatchKind.Delete, PatchPath(line, "*** Delete File:"), null, null));
                index++;
                continue;
            }

            if (line.StartsWith("*** Update File:", StringComparison.Ordinal)) {
                var path = PatchPath(line, "*** Update File:");
                index++;
                var hunks = new List<PatchHunk>();
                while (index < end && lines[index].StartsWith("@@", StringComparison.Ordinal)) {
                    var context = lines[index].Length == 2 ? null : lines[index][2..].Trim();
                    index++;
                    var oldLines = new List<string>();
                    var newLines = new List<string>();
                    var endOfFile = false;
                    while (index < end) {
                        line = lines[index];
                        if (line.StartsWith("@@", StringComparison.Ordinal)) break;
                        if (line == "*** End of File") {
                            endOfFile = true;
                            index++;
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

                        index++;
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
        FileToolSupport.TextFile source,
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
        if (source.Text.EndsWith('\n') && lines.Count > 0) content += '\n';
        return content.Replace("\n", source.Newline, StringComparison.Ordinal);
    }

    private static List<string> SplitLines(string text) {
        if (text.Length == 0) return [];
        var lines = FileToolSupport.NormalizeLineEndings(text).Split('\n').ToList();
        if (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static int FindSequence(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> sequence,
        int start) {
        if (sequence.Count == 0) return -1;
        for (var index = Math.Max(0, start); index + sequence.Count <= lines.Count; index++) {
            var matches = true;
            for (var offset = 0; offset < sequence.Count; offset++) {
                if (!string.Equals(lines[index + offset], sequence[offset], StringComparison.Ordinal)) {
                    matches = false;
                    break;
                }
            }

            if (matches) return index;
        }

        return -1;
    }

    private static string PatchPath(string line, string marker) {
        var path = line[marker.Length..].Trim();
        if (path.Length == 0) throw new InvalidOperationException($"patch path is empty after '{marker}'");
        return path;
    }

    private static string FormatLine(string line) {
        if (line.Length <= FileToolSupport.MaxReadLineLength) return line;
        var length = FileToolSupport.MaxReadLineLength;
        if (char.IsHighSurrogate(line[length - 1])) length--;
        return $"{line[..length]}... [line truncated]";
    }

    private static void EnsureRegularFile(string path, string toolName) {
        if (Directory.Exists(path)) throw new InvalidOperationException($"{toolName} path is a directory: {path}");
        if (!File.Exists(path)) throw new FileNotFoundException($"{toolName} file does not exist: {path}");
    }

    private static JsonElement Schema(string json) {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

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
}
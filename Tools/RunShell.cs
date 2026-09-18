using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Kite.Agent;

namespace Kite.Tools;

internal static partial class RunShell {
    public const string DefaultName = "run";
    private const int MaxOutputBytes = 50 * 1024;

    private static readonly (string Path, string Arg, string Dialect) Shell = ResolveShell();

    public static readonly ToolDefinition Definition = new(
        DefaultName,
        $"Run a shell command using {Shell.Dialect}.",
        JsonDocument.Parse("""
                           {
                             "type": "object",
                             "properties": {
                               "command": { "type": "string", "description": "The shell command to execute." }
                             },
                             "required": ["command"],
                             "additionalProperties": false
                           }
                           """).RootElement.Clone());

    public static Task<string> ExecuteAsync(ToolCall call, string workingDirectory, CancellationToken ct) {
        try {
            using var document = JsonDocument.Parse(call.Arguments);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("command", out var command) &&
                command.ValueKind == JsonValueKind.String) {
                return RunAsync(command.GetString() ?? throw new InvalidOperationException("Tool command is null"),
                    workingDirectory, ct);
            }
        } catch (JsonException ex) {
            throw new InvalidOperationException("Tool arguments are invalid JSON", ex);
        }

        throw new InvalidOperationException("Tool arguments do not contain a string command");
    }

    private static async Task<string> RunAsync(string command, string workingDirectory, CancellationToken ct) {
        var psi = new ProcessStartInfo(Shell.Path) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        psi.ArgumentList.Add(Shell.Arg);
        psi.ArgumentList.Add(command);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {Shell.Path}");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        try {
            await process.WaitForExitAsync(ct);
        } catch (OperationCanceledException) {
            try {
                process.Kill(entireProcessTree: true);
            } catch (InvalidOperationException) when (process.HasExited) { }

            throw;
        }

        var raw = $"{await stdout}{await stderr}".TrimEnd();
        var output = TruncateOutput(raw);
        return $"{output}\n[exit code {process.ExitCode}]".TrimStart('\n');
    }

    private static string TruncateOutput(string text) {
        if (Encoding.UTF8.GetByteCount(text) <= MaxOutputBytes) {
            return text;
        }

        // Cut on a byte budget so the limit means the same thing in every script: a character budget
        // would let multibyte text through at up to three times the advertised size.
        var remaining = MaxOutputBytes;
        var maxChars = 0;
        while (maxChars < text.Length) {
            var size = Encoding.UTF8.GetByteCount(text.AsSpan(maxChars, 1));
            if (size > remaining) break;

            remaining -= size;
            maxChars += 1;
        }

        if (maxChars > 0 && char.IsHighSurrogate(text[maxChars - 1])) {
            maxChars -= 1;
        }

        return $"{text[..maxChars]}\n[output truncated; exceeded {MaxOutputBytes / 1024} KB limit]";
    }

    private static (string Path, string Arg, string Dialect) ResolveShell() {
        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrWhiteSpace(shell)) {
            var dialect = Path.GetFileNameWithoutExtension(shell).ToLowerInvariant();
            return (shell, "-c", dialect);
        }

        if (!OperatingSystem.IsWindows()) {
            return ("/bin/sh", "-c", "sh");
        }

        var parentName = TryGetWindowsParentProcessName();
        if (parentName is not null) {
            switch (parentName.ToLowerInvariant()) {
                case "pwsh":
                    return ("pwsh.exe", "-Command", "pwsh");
                case "powershell":
                    return ("powershell.exe", "-Command", "powershell");
                case "cmd":
                    return ("cmd.exe", "/c", "cmd");
                case "bash":
                    return ("bash.exe", "-c", "bash");
            }
        }

        var psModulePath = Environment.GetEnvironmentVariable("PSModulePath");
        if (!string.IsNullOrWhiteSpace(psModulePath)) {
            if (psModulePath.Contains("PowerShell\\7", StringComparison.OrdinalIgnoreCase) ||
                psModulePath.Contains("pwsh", StringComparison.OrdinalIgnoreCase)) {
                return ("pwsh.exe", "-Command", "pwsh");
            }

            return ("powershell.exe", "-Command", "powershell");
        }

        var comspec = Environment.GetEnvironmentVariable("COMSPEC");
        var comspecPath = !string.IsNullOrWhiteSpace(comspec) ? comspec : "cmd.exe";
        return (comspecPath, "/c", "cmd");
    }

    private static string? TryGetWindowsParentProcessName() {
        if (!OperatingSystem.IsWindows()) {
            return null;
        }

        try {
            var status = NtQueryInformationProcess(
                Process.GetCurrentProcess().Handle,
                0,
                out var pbi,
                Marshal.SizeOf<ProcessBasicInformation>(),
                out _);
            if (status != 0) {
                return null;
            }

            var parentPid = checked((int)pbi.InheritedFromUniqueProcessId);
            if (parentPid <= 0) {
                return null;
            }

            using var parent = Process.GetProcessById(parentPid);
            return parent.ProcessName;
        } catch {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        out ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);
}
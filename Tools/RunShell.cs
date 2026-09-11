using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Kite.Tools;

public static partial class RunShell {
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

    public static async Task<string> RunAsync(
        string command,
        string workingDirectory,
        CancellationToken cancellationToken) {
        var psi = new ProcessStartInfo(Shell.Path) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        psi.ArgumentList.Add(Shell.Arg);
        psi.ArgumentList.Add(command);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {Shell.Path}");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        try {
            await process.WaitForExitAsync(cancellationToken);
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

        var maxChars = Math.Min(text.Length, MaxOutputBytes);
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
        if (!OperatingSystem.IsWindows()) return null;

        try {
            var status = NtQueryInformationProcess(
                Process.GetCurrentProcess().Handle,
                0,
                out var pbi,
                Marshal.SizeOf<ProcessBasicInformation>(),
                out _);
            if (status != 0) return null;
            var parentPid = checked((int)pbi.InheritedFromUniqueProcessId);
            if (parentPid <= 0) return null;
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
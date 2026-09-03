using System.Diagnostics;
using System.Text.Json;

namespace Kite.Tools;

/// <summary>
/// run: execute a bash command. No sandbox, no confirmation, no limits —
/// runs in the current working directory with the inherited environment;
/// Esc cancels and kills the process. Output is returned verbatim.
/// </summary>
public static class RunBash {
    public const string DefaultName = "run";

    public static readonly ToolDefinition Definition = new(
        DefaultName,
        "Run a bash command.",
        JsonDocument.Parse("""
                           {
                             "type": "object",
                             "properties": {
                               "command": { "type": "string", "description": "The bash command to execute." }
                             },
                             "required": ["command"],
                             "additionalProperties": false
                           }
                           """).RootElement.Clone());

    public static async Task<string> RunAsync(
        string command,
        string workingDirectory,
        CancellationToken cancellationToken) {
        var psi = new ProcessStartInfo("/bin/bash") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start bash");
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

        var output = $"{await stdout}{await stderr}".TrimEnd();
        return $"{output}\n[exit code {process.ExitCode}]".TrimStart('\n');
    }
}
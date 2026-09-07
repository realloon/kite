using System.Diagnostics;
using System.Text;

namespace Kite.Ui;

internal static class Clipboard {
    public static void SetText(string text) {
        if (string.IsNullOrEmpty(text)) return;

        // 1. OSC 52 sequence: universal terminal clipboard protocol across local and SSH sessions.
        try {
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            Console.Out.Write($"\e]52;c;{base64}\a");
            Console.Out.Flush();
        } catch { }

        // 2. OS-specific clipboard utility fallback.
        try {
            if (OperatingSystem.IsMacOS()) {
                using var process = Process.Start(new ProcessStartInfo {
                    FileName = "pbcopy",
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (process is null) return;

                process.StandardInput.Write(text);
                process.StandardInput.Close();
                process.WaitForExit(500);
            } else if (OperatingSystem.IsWindows()) {
                using var process = Process.Start(new ProcessStartInfo {
                    FileName = "clip.exe",
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (process is null) return;

                process.StandardInput.Write(text);
                process.StandardInput.Close();
                process.WaitForExit(500);
            } else if (OperatingSystem.IsLinux()) {
                var tool = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is not null ? "wl-copy" : "xclip";
                var args = tool == "xclip" ? "-selection clipboard" : "";
                using var process = Process.Start(new ProcessStartInfo {
                    FileName = tool,
                    Arguments = args,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (process is null) return;

                process.StandardInput.Write(text);
                process.StandardInput.Close();
                process.WaitForExit(500);
            }
        } catch { }
    }
}
using System.Diagnostics;
using System.Text;

namespace Kite.Ui;

internal static class Clipboard {
    public static void SetText(string text) {
        if (string.IsNullOrEmpty(text)) return;

        // OSC 52 sequence: universal terminal clipboard protocol across local and SSH sessions.
        try {
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            Console.Out.Write($"\e]52;c;{base64}\a");
            Console.Out.Flush();
        } catch (Exception) {
            // ignored
        }

        if (OperatingSystem.IsMacOS()) {
            PipeTo("pbcopy", string.Empty, text);
        } else if (OperatingSystem.IsWindows()) {
            PipeTo("clip.exe", string.Empty, text);
        } else if (OperatingSystem.IsLinux()) {
            var isWayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is not null;
            PipeTo(isWayland ? "wl-copy" : "xclip", isWayland ? string.Empty : "-selection clipboard", text);
        }
    }

    private static void PipeTo(string command, string arguments, string text) {
        try {
            using var process = Process.Start(new ProcessStartInfo {
                FileName = command,
                Arguments = arguments,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return;

            process.StandardInput.Write(text);
            process.StandardInput.Close();
            process.WaitForExit(500);
        } catch (Exception) {
            // ignored
        }
    }
}
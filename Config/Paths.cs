namespace Kite.Config;

internal static class Paths {
    public static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static StringComparer Comparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kite");

    public static string Presets => Path.Combine(DataDirectory, "presets.json");
    public static string Auth => Path.Combine(DataDirectory, "auth.json");
    public static string State => Path.Combine(DataDirectory, "state.json");
    public static string Sessions => Path.Combine(DataDirectory, "sessions");
    public static string GlobalSkills => Path.Combine(DataDirectory, "skills");
    public static string GlobalInstructions => Path.Combine(DataDirectory, "AGENTS.md");

    public static void EnsurePrivateDirectory(string path) {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows()) return;

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static void SetPrivateFileMode(string path) {
        if (OperatingSystem.IsWindows()) return;

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
using System.Text.Json;

namespace Kite.Configuration;

/// <summary>
/// Local config (~/.kite/config.json, mode 0600) — configuration layer 2.
/// Layer 1 is the built-in preset catalog (ModelCatalog, embedded in the
/// binary); anything set here overrides the matching preset. ApiKey is
/// written by /connect.
/// </summary>
public sealed class KiteConfig {
    public string? Provider { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>
    /// Model to use: a built-in preset id (inherits the preset's limits,
    /// instructions and cost) or any raw model name (fully custom —
    /// that requires baseUrl from config).
    /// </summary>
    public string? Model { get; set; }

    /// <summary>Overrides the preset's base URL.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Overrides the preset's instructions; empty string disables them.</summary>
    public string? Instructions { get; set; }

    /// <summary>The chosen variant, picked via /variants; validated against the model's preset list at build time.</summary>
    public string? Variants { get; set; }

    public static string DataDirectory {
        get {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return System.IO.Path.Combine(home, ".kite");
        }
    }

    public static string Path => System.IO.Path.Combine(DataDirectory, "config.json");

    public static KiteConfig Load() {
        if (!File.Exists(Path)) {
            return new KiteConfig();
        }

        try {
            var json = File.ReadAllText(Path);
            return JsonSerializer.Deserialize(json, KiteJsonContext.Default.KiteConfig) ?? new KiteConfig();
        } catch (Exception ex) {
            throw new InvalidOperationException($"Config file is invalid {Path}: {ex.Message}", ex);
        }
    }

    public void Save() {
        try {
            var dir = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows()) {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            File.WriteAllText(Path, JsonSerializer.Serialize(this, KiteJsonContext.Default.KiteConfig));
            if (!OperatingSystem.IsWindows()) {
                File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        } catch (Exception ex) {
            throw new InvalidOperationException($"Could not save config {Path}: {ex.Message}", ex);
        }
    }

    public bool HasDeepSeekKey =>
        string.Equals(Provider, "deepseek", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(ApiKey);
}

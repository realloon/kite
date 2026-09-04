using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Kite.Configuration;

/// <summary>
/// Local preset layer (~/.kite/config.json) — configuration layer 2.
/// Layer 1 is the built-in preset catalog (ModelCatalog, embedded in the
/// binary); entries here override matching providers/models or add new ones.
/// </summary>
public sealed class KiteConfig {
    public List<ProviderPreset>? Providers { get; set; }

    public static string DataDirectory {
        get {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return System.IO.Path.Combine(home, ".kite");
        }
    }

    public static string Path => System.IO.Path.Combine(DataDirectory, "config.json");

    public static KiteConfig Load() {
        return JsonFile.Load(Path, KiteJsonContext.Default.KiteConfig, "config");
    }
}

internal static class JsonFile {
    public static T Load<T>(string path, JsonTypeInfo<T> typeInfo, string label) where T : new() {
        if (!File.Exists(path)) return new T();

        try {
            return JsonSerializer.Deserialize(File.ReadAllText(path), typeInfo) ?? new T();
        } catch (Exception ex) {
            throw new InvalidOperationException($"{label} file is invalid {path}: {ex.Message}", ex);
        }
    }

    public static void Save<T>(string path, T value, JsonTypeInfo<T> typeInfo, string label) {
        try {
            Directory.CreateDirectory(KiteConfig.DataDirectory);
            if (!OperatingSystem.IsWindows()) {
                File.SetUnixFileMode(
                    KiteConfig.DataDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(value, typeInfo));
            if (!OperatingSystem.IsWindows()) {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        } catch (Exception ex) {
            throw new InvalidOperationException(
                $"Could not save {label} {path}: {ex.Message}", ex);
        }
    }
}
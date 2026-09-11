using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Kite.Configuration;

internal static class JsonFile {
    public static T Load<T>(string path, JsonTypeInfo<T> typeInfo, string label) where T : new() {
        if (!File.Exists(path)) {
            return new T();
        }

        try {
            return JsonSerializer.Deserialize(File.ReadAllText(path), typeInfo) ?? new T();
        } catch (Exception ex) {
            throw new InvalidOperationException($"{label} file is invalid {path}: {ex.Message}", ex);
        }
    }

    public static void Save<T>(string path, T value, JsonTypeInfo<T> typeInfo, string label) {
        try {
            Directory.CreateDirectory(Paths.DataDirectory);
            if (!OperatingSystem.IsWindows()) {
                File.SetUnixFileMode(
                    Paths.DataDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(value, typeInfo));
            if (!OperatingSystem.IsWindows()) {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        } catch (Exception ex) {
            throw new InvalidOperationException($"Could not save {label} {path}: {ex.Message}", ex);
        }
    }
}
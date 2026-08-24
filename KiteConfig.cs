using System.Text.Json;
using System.Text.Json.Serialization;
using Kite.Agent;

namespace Kite;

/// <summary>
/// Local config (~/.kite/config.json, mode 0600).
/// Only DeepSeek is supported for now; ApiKey is written by /connect.
/// </summary>
public sealed class KiteConfig {
    public string? Provider { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>Reasoning effort sent as-is to the API (raw value, see /variants).</summary>
    public string? ReasoningEffort { get; set; }

    public static string Path {
        get {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return System.IO.Path.Combine(home, ".kite", "config.json");
        }
    }

    public static KiteConfig Load() {
        try {
            if (File.Exists(Path)) {
                var json = File.ReadAllText(Path);
                return JsonSerializer.Deserialize(json, KiteJsonContext.Default.KiteConfig) ?? new KiteConfig();
            }
        } catch {
            // Corrupt config: treat as unconfigured, do not block startup
        }

        return new KiteConfig();
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
            throw new InvalidOperationException($"无法保存配置 {Path}：{ex.Message}", ex);
        }
    }

    /// <summary>Valid config: DeepSeek provider with a non-empty key.</summary>
    public bool HasDeepSeekKey =>
        string.Equals(Provider, "deepseek", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>AOT-safe JSON context (request DTOs + local config).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ResponsesAgent.ResponsesRequest))]
[JsonSerializable(typeof(ToolDefinition))]
[JsonSerializable(typeof(KiteConfig))]
internal sealed partial class KiteJsonContext : JsonSerializerContext;
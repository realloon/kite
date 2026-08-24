using System.Text.Json;
using System.Text.Json.Serialization;
using Kite.Agent;

namespace Kite;

/// <summary>
/// Local config (~/.kite/config.json, mode 0600) — configuration layer 2.
/// Layer 1 is the built-in preset catalog (ModelCatalog, embedded in the
/// binary); anything set here overrides the matching preset, and KITE_* env
/// vars override this. ApiKey is written by /connect.
/// </summary>
public sealed class KiteConfig {
    public string? Provider { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>
    /// Model to use: a built-in preset id (inherits the preset's limits,
    /// instructions and cost) or any raw model name (fully custom —
    /// that requires baseUrl from config or KITE_BASE_URL).
    /// </summary>
    public string? Model { get; set; }

    /// <summary>Overrides the preset's base URL.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Overrides the preset's instructions; empty string disables them.</summary>
    public string? Instructions { get; set; }

    /// <summary>Reasoning effort, picked via /variants; validated against the model's preset list at build time.</summary>
    public string? ReasoningEffort { get; set; }

    public static string Path {
        get {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return System.IO.Path.Combine(home, ".kite", "config.json");
        }
    }

    public static KiteConfig Load() {
        if (!File.Exists(Path)) {
            return new KiteConfig();
        }

        try {
            var json = File.ReadAllText(Path);
            return JsonSerializer.Deserialize(json, KiteJsonContext.Default.KiteConfig) ?? new KiteConfig();
        } catch (Exception ex) {
            throw new InvalidOperationException($"配置文件损坏 {Path}：{ex.Message}", ex);
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
            throw new InvalidOperationException($"无法保存配置 {Path}：{ex.Message}", ex);
        }
    }

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
[JsonSerializable(typeof(PresetFile))]
internal sealed partial class KiteJsonContext : JsonSerializerContext;
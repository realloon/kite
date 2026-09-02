using System.Text.Json;

namespace Kite;

/// <summary>
/// Layer-1 model catalog: presets.json resource embedded in the assembly is
/// the single source of truth. There is no code-side copy and no fallback,
/// and which model to use is never decided here — the user config (layer 2)
/// picks it. A missing/corrupt resource, an empty catalog or an invalid
/// preset is a packaging error and throws at startup.
/// </summary>
public static class ModelCatalog {
    private const string ResourceName = "Kite.presets.json";

    private static readonly PresetFile File = Load();

    /// <summary>Presets after load-time validation; touching this type loads and validates the catalog.</summary>
    public static IReadOnlyList<ModelPreset> Presets { get; } = [.. File.Providers!.SelectMany(p => p.Models!)];

    public static ModelPreset? Find(string? modelId) {
        return string.IsNullOrWhiteSpace(modelId)
            ? null
            : Presets.FirstOrDefault(preset => preset.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
    }

    private static PresetFile Load() {
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException(
                               $"缺少内嵌资源 {ResourceName}：构建产物不完整，请重新构建");

        using var reader = new StreamReader(stream);

        PresetFile file;
        try {
            file = JsonSerializer.Deserialize(reader.ReadToEnd(), KiteJsonContext.Default.PresetFile)
                   ?? throw new InvalidOperationException($"解析 {ResourceName} 失败：内容为空");
        } catch (JsonException ex) {
            throw new InvalidOperationException($"解析 {ResourceName} 失败：{ex.Message}", ex);
        }

        if (file.Providers is not { Count: > 0 }) {
            throw new InvalidOperationException("presets.json 未包含任何 provider");
        }

        var seenModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in file.Providers) {
            if (string.IsNullOrWhiteSpace(provider.Id)) {
                throw new InvalidOperationException("presets.json 存在缺少 id 的 provider");
            }

            if (string.IsNullOrWhiteSpace(provider.BaseUrl)) {
                throw new InvalidOperationException($"presets.json 的 provider '{provider.Id}' 缺少 baseUrl");
            }

            if (provider.Models is not { Count: > 0 }) {
                throw new InvalidOperationException($"presets.json 的 provider '{provider.Id}' 未包含任何模型");
            }

            foreach (var model in provider.Models) {
                if (string.IsNullOrWhiteSpace(model.Id)) {
                    throw new InvalidOperationException($"presets.json 的 provider '{provider.Id}' 存在缺少 id 的模型");
                }

                if (!seenModels.Add(model.Id)) {
                    throw new InvalidOperationException($"模型 '{model.Id}' 在预设目录中重复");
                }

                model.BaseUrl = provider.BaseUrl;
                if (model.Instructions is not null) {
                    model.Instructions = PromptStore.Resolve(model.Instructions);
                }

                RequireLimits(model);
                RequireFullCost(model);
                RequireVariants(model);
            }
        }

        return file;
    }

    private static void RequireLimits(ModelPreset preset) {
        var limits = preset.Limit ?? throw new InvalidOperationException($"presets.json 的模型 '{preset.Id}' 缺少 limit");

        var missing = new List<string>();
        if (limits.Context is null) missing.Add("context");
        if (limits.Output is null) missing.Add("output");
        if (missing.Count > 0) {
            throw new InvalidOperationException(
                $"presets.json 的模型 '{preset.Id}' 的 limit 缺少字段：{string.Join("、", missing)}");
        }
    }

    private static void RequireFullCost(ModelPreset preset) {
        var cost = preset.Cost ?? throw new InvalidOperationException($"presets.json 的模型 '{preset.Id}' 缺少 cost");
        var missing = new List<string>();

        if (cost.Input is null) missing.Add("input");
        if (cost.Output is null) missing.Add("output");
        if (cost.CacheWrite is null) missing.Add("cache_write");
        if (cost.CacheRead is null) missing.Add("cache_read");
        if (missing.Count > 0) {
            throw new InvalidOperationException(
                $"presets.json 的模型 '{preset.Id}' 的 cost 缺少字段：{string.Join("、", missing)}");
        }
    }

    private static void RequireVariants(ModelPreset preset) {
        if (preset.Variants is not { Count: > 0 }) {
            throw new InvalidOperationException(
                $"presets.json 的模型 '{preset.Id}' 缺少 variants");
        }
    }
}

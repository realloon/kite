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
                               $"Missing embedded resource {ResourceName}. The build is incomplete; rebuild the app.");

        using var reader = new StreamReader(stream);

        PresetFile file;
        try {
            file = JsonSerializer.Deserialize(reader.ReadToEnd(), KiteJsonContext.Default.PresetFile)
                   ?? throw new InvalidOperationException($"Could not parse {ResourceName}: empty content");
        } catch (JsonException ex) {
            throw new InvalidOperationException($"Could not parse {ResourceName}: {ex.Message}", ex);
        }

        if (file.Providers is not { Count: > 0 }) {
            throw new InvalidOperationException("presets.json has no providers");
        }

        var seenModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in file.Providers) {
            if (string.IsNullOrWhiteSpace(provider.Id)) {
                throw new InvalidOperationException("presets.json contains a provider without an id");
            }

            if (string.IsNullOrWhiteSpace(provider.BaseUrl)) {
                throw new InvalidOperationException($"Provider '{provider.Id}' in presets.json has no baseUrl");
            }

            if (provider.Models is not { Count: > 0 }) {
                throw new InvalidOperationException($"Provider '{provider.Id}' in presets.json has no models");
            }

            foreach (var model in provider.Models) {
                if (string.IsNullOrWhiteSpace(model.Id)) {
                    throw new InvalidOperationException($"Provider '{provider.Id}' in presets.json contains a model without an id");
                }

                if (!seenModels.Add(model.Id)) {
                    throw new InvalidOperationException($"Model '{model.Id}' is duplicated in presets.json");
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
        var limits = preset.Limit ?? throw new InvalidOperationException($"Model '{preset.Id}' in presets.json has no limit");

        var missing = new List<string>();
        if (limits.Context is null) missing.Add("context");
        if (limits.Output is null) missing.Add("output");
        if (missing.Count > 0) {
            throw new InvalidOperationException(
                $"Model '{preset.Id}' in presets.json is missing limit fields: {string.Join(", ", missing)}");
        }
    }

    private static void RequireFullCost(ModelPreset preset) {
        var cost = preset.Cost ?? throw new InvalidOperationException($"Model '{preset.Id}' in presets.json has no cost");
        var missing = new List<string>();

        if (cost.Input is null) missing.Add("input");
        if (cost.Output is null) missing.Add("output");
        if (cost.CacheWrite is null) missing.Add("cache_write");
        if (cost.CacheRead is null) missing.Add("cache_read");
        if (missing.Count > 0) {
            throw new InvalidOperationException(
                $"Model '{preset.Id}' in presets.json is missing cost fields: {string.Join(", ", missing)}");
        }
    }

    private static void RequireVariants(ModelPreset preset) {
        if (preset.Variants is not { Count: > 0 }) {
            throw new InvalidOperationException(
                $"Model '{preset.Id}' in presets.json has no variants");
        }
    }
}

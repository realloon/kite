using System.Text.Json;

namespace Kite.Configuration;

/// <summary>
/// Runtime model catalog: the embedded presets are merged with the user's
/// sparse preset layer, then the result is validated before use.
/// </summary>
public sealed class ModelCatalog {
    private const string ResourceName = "kite.presets.json";

    public ModelCatalog(KiteConfig userConfig) {
        var catalog = LoadBuiltIn();
        Merge(catalog, userConfig);
        Validate(catalog.Providers);
        Providers = catalog.Providers;
    }

    public IReadOnlyList<ProviderPreset> Providers { get; }

    public ProviderPreset? FindProvider(string? providerId) => string.IsNullOrWhiteSpace(providerId)
        ? null
        : Providers.FirstOrDefault(provider => provider.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));

    public ModelPreset? FindModel(string? providerId, string? modelId) => FindProvider(providerId)?.Models
        .FirstOrDefault(model => string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<(ProviderPreset Provider, ModelPreset Model)> Models => Providers
        .SelectMany(provider => provider.Models.Select(model => (provider, model)));

    private static KiteConfig LoadBuiltIn() {
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException(
                               $"Missing embedded resource {ResourceName}. The build is incomplete; rebuild the app.");

        using var reader = new StreamReader(stream);

        KiteConfig catalog;
        try {
            catalog = JsonSerializer.Deserialize(reader.ReadToEnd(), KiteJsonContext.Default.KiteConfig)
                      ?? throw new InvalidOperationException($"Could not parse {ResourceName}: empty content");
        } catch (JsonException ex) {
            throw new InvalidOperationException($"Could not parse {ResourceName}: {ex.Message}", ex);
        }

        if (catalog.Providers.Count == 0) {
            throw new InvalidOperationException("presets.json has no providers");
        }

        foreach (var provider in catalog.Providers) {
            foreach (var model in provider.Models) {
                if (string.IsNullOrWhiteSpace(model.Instructions)) {
                    model.Instructions = "$default";
                }

                model.Instructions = PromptStore.Resolve(model.Instructions);
            }
        }

        return catalog;
    }

    private static void Merge(KiteConfig catalog, KiteConfig userConfig) {
        var providers = catalog.Providers;
        var seenUserProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var userProvider in userConfig.Providers) {
            if (!seenUserProviders.Add(userProvider.Id)) {
                throw new InvalidOperationException(
                    $"config.json contains provider '{userProvider.Id}' more than once");
            }

            var provider = providers.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, userProvider.Id, StringComparison.OrdinalIgnoreCase));
            if (provider is null) {
                providers.Add(userProvider);
                continue;
            }

            provider.BaseUrl = userProvider.BaseUrl ?? provider.BaseUrl;
            if (userProvider.Models.Count == 0) continue;

            var models = provider.Models;
            var seenUserModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var userModel in userProvider.Models) {
                if (!seenUserModels.Add(userModel.Id)) {
                    throw new InvalidOperationException(
                        $"config.json contains model '{userModel.Id}' more than once for provider '{provider.Id}'");
                }

                var index = models.FindIndex(model =>
                    string.Equals(model.Id, userModel.Id, StringComparison.OrdinalIgnoreCase));
                if (index < 0) {
                    models.Add(userModel);
                } else {
                    models[index] = Merge(models[index], userModel);
                }
            }
        }
    }

    private static ModelPreset Merge(ModelPreset preset, ModelPreset overridePreset) => new() {
        Id = preset.Id,
        BaseUrl = overridePreset.BaseUrl ?? preset.BaseUrl,
        Limit = overridePreset.Limit ?? preset.Limit,
        Instructions = !string.IsNullOrWhiteSpace(overridePreset.Instructions)
            ? overridePreset.Instructions
            : preset.Instructions,
        Variants = overridePreset.Variants.Count > 0 ? overridePreset.Variants : preset.Variants,
        Tools = overridePreset.Tools.Count > 0 ? overridePreset.Tools : preset.Tools,
        Cost = overridePreset.Cost ?? preset.Cost
    };

    private static void Validate(List<ProviderPreset> providers) {
        var seenProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers) {
            if (string.IsNullOrWhiteSpace(provider.Id)) {
                throw new InvalidOperationException("presets.json contains a provider without an id");
            }

            if (!seenProviders.Add(provider.Id)) {
                throw new InvalidOperationException($"Provider '{provider.Id}' is duplicated in presets");
            }

            if (string.IsNullOrWhiteSpace(provider.BaseUrl)) {
                throw new InvalidOperationException($"Provider '{provider.Id}' has no baseUrl");
            }

            if (provider.Models.Count == 0) {
                throw new InvalidOperationException($"Provider '{provider.Id}' has no models");
            }

            var seenModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in provider.Models) {
                if (string.IsNullOrWhiteSpace(model.Id)) {
                    throw new InvalidOperationException(
                        $"Provider '{provider.Id}' contains a model without an id");
                }

                if (!seenModels.Add(model.Id)) {
                    throw new InvalidOperationException(
                        $"Model '{model.Id}' is duplicated for provider '{provider.Id}'");
                }

                model.BaseUrl ??= provider.BaseUrl;
                if (string.IsNullOrWhiteSpace(model.BaseUrl)) {
                    throw new InvalidOperationException(
                        $"Model '{model.Id}' in provider '{provider.Id}' has no baseUrl");
                }

                if (string.IsNullOrWhiteSpace(model.Instructions)) {
                    model.Instructions = PromptStore.Resolve("$default");
                } else if (model.Instructions.StartsWith('$')) {
                    throw new InvalidOperationException(
                        $"config.json model '{model.Id}' cannot use '$' prompt references");
                }

                RequireLimits(model);
                RequireFullCost(model);
                ValidateVariants(model);
                RequireTools(model);
            }
        }
    }

    private static void RequireTools(ModelPreset preset) {
        foreach (var tool in preset.Tools) {
            if (tool.ValueKind != JsonValueKind.Object ||
                !tool.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(type.GetString())) {
                throw new InvalidOperationException(
                    $"Model '{preset.Id}' contains an invalid tool; each tool needs a non-empty string type");
            }
        }
    }

    private static void RequireLimits(ModelPreset preset) {
        var limits = preset.Limit ?? throw new InvalidOperationException($"Model '{preset.Id}' has no limit");

        var missing = new List<string>();
        if (limits.Context is null) missing.Add("context");
        if (limits.Output is null) missing.Add("output");
        if (missing.Count > 0) {
            throw new InvalidOperationException(
                $"Model '{preset.Id}' is missing limit fields: {string.Join(", ", missing)}");
        }
    }

    private static void RequireFullCost(ModelPreset preset) {
        var cost = preset.Cost ?? throw new InvalidOperationException($"Model '{preset.Id}' has no cost");
        if (string.IsNullOrWhiteSpace(cost.Currency)) {
            cost.Currency = "$";
        }

        if (cost.Peak is null && (cost.Input is not null || cost.Output is not null || cost.CacheRead is not null)) {
            cost.Peak = new ModelPrice {
                Input = cost.Input,
                Output = cost.Output,
                CacheWrite = cost.CacheWrite,
                CacheRead = cost.CacheRead
            };
        }

        if (cost.Peak is null) {
            throw new InvalidOperationException($"Model '{preset.Id}' has no cost prices configured");
        }

        cost.OffPeak ??= new ModelPrice {
            Input = cost.Peak.Input,
            Output = cost.Peak.Output,
            CacheWrite = cost.Peak.CacheWrite,
            CacheRead = cost.Peak.CacheRead
        };

        RequirePrice(preset, "peak", cost.Peak);
        RequirePrice(preset, "off_peak", cost.OffPeak);
    }

    private static void RequirePrice(ModelPreset preset, string period, ModelPrice? price) {
        if (price is null) {
            throw new InvalidOperationException($"Model '{preset.Id}' has no {period} cost");
        }

        var missing = new List<string>();
        if (price.Input is null) {
            missing.Add("input");
        }

        if (price.Output is null) {
            missing.Add("output");
        }

        if (price.CacheRead is null) {
            missing.Add("cache_read");
        }

        if (missing.Count > 0) {
            throw new InvalidOperationException(
                $"Model '{preset.Id}' is missing {period} cost fields: {string.Join(", ", missing)}");
        }

        if (price.Input < 0 || price.Output < 0 || price.CacheWrite < 0 || price.CacheRead < 0) {
            throw new InvalidOperationException($"Model '{preset.Id}' has a negative {period} cost");
        }
    }

    private static void ValidateVariants(ModelPreset preset) {
        if (preset.Variants.Any(string.IsNullOrWhiteSpace)) {
            throw new InvalidOperationException($"Model '{preset.Id}' contains an empty variant");
        }
    }
}
using System.Text.Json;

namespace Kite.Config;

internal sealed class ModelCatalog {
    private const string ResourceName = "kite.presets.json";
    private const int DefaultContextLimit = 32_768;
    private const int DefaultOutputLimit = 4_096;

    public ModelCatalog(Presets userPresets) {
        var builtIn = LoadBuiltIn();
        Merge(builtIn.Providers, userPresets.Providers);
        Providers = BuildProviders(builtIn.Providers);
    }

    public IReadOnlyList<ProviderPreset> Providers { get; }

    public ProviderPreset? FindProvider(string? providerId) => string.IsNullOrWhiteSpace(providerId)
        ? null
        : Providers.FirstOrDefault(provider => provider.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));

    public ModelPreset? FindModel(string? providerId, string? modelId) => FindProvider(providerId)?.Models
        .FirstOrDefault(model => model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<(ProviderPreset Provider, ModelPreset Model)> Models => Providers
        .SelectMany(provider => provider.Models.Select(model => (provider, model)));

    public (ProviderPreset Provider, ModelPreset Model) ResolveSelection(KiteState state) {
        state.Validate();
        if (state.Provider is null || state.Model is null) {
            throw new InvalidOperationException("state has no current model");
        }

        var provider = FindProvider(state.Provider)
                       ?? throw new InvalidOperationException($"Unknown provider '{state.Provider}' in state.json");
        var model = FindModel(provider.Id, state.Model)
                    ?? throw new InvalidOperationException(
                        $"Unknown model '{state.Model}' for provider '{provider.Id}' in state.json");
        return (provider, model);
    }

    private static Presets LoadBuiltIn() {
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException(
                               $"Missing embedded resource {ResourceName}. The build is incomplete; rebuild the app.");
        using var reader = new StreamReader(stream);

        Presets catalog;
        try {
            catalog = JsonSerializer.Deserialize(reader.ReadToEnd(), ConfigJsonContext.Default.Presets)
                      ?? throw new InvalidOperationException($"Could not parse {ResourceName}: empty content");
        } catch (JsonException ex) {
            throw new InvalidOperationException($"Could not parse {ResourceName}: {ex.Message}", ex);
        }

        if (catalog.Providers.Count == 0) {
            throw new InvalidOperationException("presets.json has no providers");
        }

        foreach (var provider in catalog.Providers) {
            foreach (var model in provider.Models ?? []) {
                var instructions = string.IsNullOrWhiteSpace(model.Instructions) ? "$default" : model.Instructions;
                model.Instructions = PromptStore.Resolve(instructions);
            }
        }

        return catalog;
    }

    private static void Merge(List<RawProviderPreset> providers, List<RawProviderPreset> userProviders) {
        var seenUserProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var userProvider in userProviders) {
            var providerId = RequireNonEmpty(userProvider.Id, "presets contain a provider without an id");
            if (!seenUserProviders.Add(providerId)) {
                throw new InvalidOperationException(
                    $"{Paths.Presets} contains provider '{providerId}' more than once");
            }

            var provider = providers.FirstOrDefault(candidate =>
                candidate.Id is not null && candidate.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
            if (provider is null) {
                providers.Add(userProvider);
                continue;
            }

            provider.BaseUrl = userProvider.BaseUrl ?? provider.BaseUrl;
            if (userProvider.Models is not { Count: > 0 }) continue;

            provider.Models ??= [];
            var seenUserModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var userModel in userProvider.Models) {
                var modelId = RequireNonEmpty(userModel.Id,
                    $"Provider '{providerId}' contains a model without an id");
                if (!seenUserModels.Add(modelId)) {
                    throw new InvalidOperationException(
                        $"{Paths.Presets} contains model '{modelId}' more than once for provider '{providerId}'");
                }

                var index = provider.Models.FindIndex(model =>
                    model.Id is not null && model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
                if (index < 0) {
                    provider.Models.Add(userModel);
                } else {
                    provider.Models[index] = MergeModel(provider.Models[index], userModel);
                }
            }
        }
    }

    private static RawModelPreset MergeModel(RawModelPreset preset, RawModelPreset overridePreset) => new() {
        Id = preset.Id,
        Api = overridePreset.Api ?? preset.Api,
        BaseUrl = overridePreset.BaseUrl ?? preset.BaseUrl,
        Limit = overridePreset.Limit is null
            ? preset.Limit
            : new RawModelLimit {
                Context = overridePreset.Limit.Context ?? preset.Limit?.Context,
                Output = overridePreset.Limit.Output ?? preset.Limit?.Output
            },
        Instructions = !string.IsNullOrWhiteSpace(overridePreset.Instructions)
            ? overridePreset.Instructions
            : preset.Instructions,
        Variants = overridePreset.Variants is { Count: > 0 } ? overridePreset.Variants : preset.Variants,
        Tools = overridePreset.Tools is { Count: > 0 } ? overridePreset.Tools : preset.Tools,
        Cost = overridePreset.Cost ?? preset.Cost
    };

    private static List<ProviderPreset> BuildProviders(List<RawProviderPreset> rawProviders) {
        var result = new List<ProviderPreset>(rawProviders.Count);
        var seenProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawProvider in rawProviders) {
            var providerId = RequireNonEmpty(rawProvider.Id, "presets contain a provider without an id");
            if (!seenProviders.Add(providerId)) {
                throw new InvalidOperationException($"Provider '{providerId}' is duplicated in presets");
            }

            var providerBaseUrl = RequireNonEmpty(rawProvider.BaseUrl, $"Provider '{providerId}' has no baseUrl");
            if (rawProvider.Models is not { Count: > 0 }) {
                throw new InvalidOperationException($"Provider '{providerId}' has no models");
            }

            var models = new List<ModelPreset>(rawProvider.Models.Count);
            var seenModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawModel in rawProvider.Models) {
                var modelId = RequireNonEmpty(rawModel.Id,
                    $"Provider '{providerId}' contains a model without an id");
                if (!seenModels.Add(modelId)) {
                    throw new InvalidOperationException(
                        $"Model '{modelId}' is duplicated for provider '{providerId}'");
                }

                var api = ParseApi(modelId, rawModel.Api);
                var baseUrl = RequireNonEmpty(rawModel.BaseUrl ?? providerBaseUrl,
                    $"Model '{modelId}' in provider '{providerId}' has no baseUrl");
                var instructions = ResolveUserInstructions(modelId, rawModel.Instructions);
                var limit = new ModelLimit(
                    rawModel.Limit?.Context ?? DefaultContextLimit,
                    rawModel.Limit?.Output ?? DefaultOutputLimit);
                var variants = rawModel.Variants ?? [];
                if (variants.Any(string.IsNullOrWhiteSpace)) {
                    throw new InvalidOperationException($"Model '{modelId}' contains an empty variant");
                }

                var tools = rawModel.Tools ?? [];
                ValidateTools(modelId, tools);
                var cost = BuildCost(modelId, rawModel.Cost);

                models.Add(new ModelPreset(modelId, api, providerId, baseUrl, limit, instructions, variants, tools,
                    cost));
            }

            result.Add(new ProviderPreset(providerId, providerBaseUrl, models));
        }

        return result;
    }

    private static ModelApi ParseApi(string modelId, string? api) => (api ?? "responses").ToLowerInvariant() switch {
        "responses" => ModelApi.Responses,
        "completions" => ModelApi.Completions,
        _ => throw new InvalidOperationException(
            $"Model '{modelId}' has unsupported api '{api}'; expected responses or completions")
    };

    private static string ResolveUserInstructions(string modelId, string? instructions) {
        if (string.IsNullOrWhiteSpace(instructions)) {
            return PromptStore.Resolve("$default");
        }

        if (instructions.StartsWith('$')) {
            throw new InvalidOperationException($"model '{modelId}' cannot use '$' prompt references");
        }

        return instructions;
    }

    private static void ValidateTools(string modelId, List<JsonElement> tools) {
        foreach (var tool in tools) {
            if (tool.ValueKind != JsonValueKind.Object ||
                !tool.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(type.GetString())) {
                throw new InvalidOperationException(
                    $"Model '{modelId}' contains an invalid tool; each tool needs a non-empty string type");
            }
        }
    }

    private static ModelCost BuildCost(string modelId, RawModelCost? rawCost) {
        if (rawCost is null) {
            throw new InvalidOperationException($"Model '{modelId}' has no cost");
        }

        var currency = string.IsNullOrWhiteSpace(rawCost.Currency) ? "$" : rawCost.Currency;
        var rawPeak = rawCost.Peak;
        if (rawPeak is null && (rawCost.Input is not null || rawCost.Output is not null ||
                                rawCost.CacheRead is not null)) {
            rawPeak = new RawModelPrice {
                Input = rawCost.Input,
                Output = rawCost.Output,
                CacheWrite = rawCost.CacheWrite,
                CacheRead = rawCost.CacheRead
            };
        }

        if (rawPeak is null) {
            throw new InvalidOperationException($"Model '{modelId}' has no cost prices configured");
        }

        var peak = BuildPrice(modelId, "peak", rawPeak);
        var offPeak = rawCost.OffPeak is null ? peak : BuildPrice(modelId, "off_peak", rawCost.OffPeak);
        return new ModelCost(currency, peak, offPeak);
    }

    private static ModelPrice BuildPrice(string modelId, string period, RawModelPrice raw) {
        var missing = new List<string>();
        if (raw.Input is null) {
            missing.Add("input");
        }

        if (raw.Output is null) {
            missing.Add("output");
        }

        if (raw.CacheRead is null) {
            missing.Add("cache_read");
        }

        if (missing.Count > 0) {
            throw new InvalidOperationException(
                $"Model '{modelId}' is missing {period} cost fields: {string.Join(", ", missing)}");
        }

        var input = raw.Input!.Value;
        var output = raw.Output!.Value;
        var cacheRead = raw.CacheRead!.Value;
        var cacheWrite = raw.CacheWrite ?? 0;
        if (input < 0 || output < 0 || cacheWrite < 0 || cacheRead < 0) {
            throw new InvalidOperationException($"Model '{modelId}' has a negative {period} cost");
        }

        return new ModelPrice(input, output, cacheRead, cacheWrite);
    }

    private static string RequireNonEmpty(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value;
}
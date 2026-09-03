using Kite.Configuration;

namespace Kite.Agent;

/// <summary>
/// Single entry point for building agents from the preset catalog and user
/// config. The catalog never picks a model; user config must name it
/// explicitly. Unknown models are fully custom and must provide a base URL.
/// Used at startup (Program), after /connect and /variants (KiteApp).
/// </summary>
public static class AgentFactory {
    /// <summary>
    /// Build the startup agent: null when no API key is configured — the app
    /// then runs in an explicit unconfigured state and /connect installs the
    /// agent at runtime. No demo/fallback agent exists.
    /// </summary>
    public static IAgent? FromConfig(KiteConfig? config = null) {
        config ??= new KiteConfig();
        var key = config.ApiKey;
        return string.IsNullOrEmpty(key) ? null : CreateDeepSeek(key, config: config);
    }

    public static ResponsesAgent CreateDeepSeek(
        string apiKey,
        string? reasoningEffort = null,
        KiteConfig? config = null) {
        config ??= new KiteConfig();

        var model = FirstNonEmpty(config.Model)
                    ?? throw new InvalidOperationException(
                        $"No model configured. Set model in ~/.kite/config.json. Presets: {PresetIds()}; custom names are also allowed.");
        var preset = ModelCatalog.Find(model);

        var baseUrl = FirstNonEmpty(config.BaseUrl, preset?.BaseUrl)
                      ?? throw new InvalidOperationException(
                          $"Unknown model '{model}'. Set baseUrl in ~/.kite/config.json.");

        // Instructions: only what is explicitly configured; null means
        // "no instructions" and nothing is sent. "$name" prompt references
        // belong to the app-side catalog; a user-side "$..." is an error,
        // never a literal.
        if (config.Instructions is { } configInstructions && configInstructions.StartsWith('$')) {
            throw new InvalidOperationException(
                "config.instructions does not support '$' references. Write the prompt text directly.");
        }

        var instructions = config.Instructions ?? preset?.Instructions;

        var effort = reasoningEffort ?? config.Variants;
        var variants = preset?.Variants;
        if (effort is not null && variants is not null &&
            !variants.Contains(effort, StringComparer.OrdinalIgnoreCase)) {
            throw new InvalidOperationException(
                $"Model '{model}' does not support reasoning effort '{effort}'. Available: {string.Join(" / ", variants)}");
        }

        var maxOutputTokens = preset?.Limit?.Output;

        return new ResponsesAgent(
            apiKey,
            baseUrl: baseUrl,
            model: model,
            instructions: instructions,
            reasoningEffort: effort,
            maxOutputTokens: maxOutputTokens);
    }

    private static string? FirstNonEmpty(params string?[] values) {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string PresetIds() =>
        string.Join(", ", ModelCatalog.Presets.Select(p => p.Id));
}
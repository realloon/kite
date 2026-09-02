using Kite.Agent;

namespace Kite;

/// <summary>
/// Single entry point for building agents, resolving the two configuration
/// layers with no hidden defaults: layer 1 is the preset catalog (presets.json,
/// embedded — a pure catalog, it never picks a model), layer 2 is the user
/// config (~/.kite/config.json) and KITE_* env vars, which must name the
/// model explicitly. Precedence: env vars > user config > preset. Unknown
/// models are fully custom: every field must come from config/env, or the
/// build fails loudly.
/// Used at startup (Program), after /connect and /variants (KiteApp).
/// </summary>
public static class AgentFactory {
    /// <summary>
    /// Build the startup agent: null when no API key is configured — the app
    /// then runs in an explicit unconfigured state and /connect installs the
    /// agent at runtime. No demo/fallback agent exists.
    /// </summary>
    public static IAgent? FromEnvOrConfig(KiteConfig? config = null) {
        config ??= new KiteConfig();
        var key = CurrentKey(config);
        return string.IsNullOrEmpty(key) ? null : CreateDeepSeek(key, config: config);
    }

    /// <summary>
    /// API key: env var or the local config — no other source exists.
    /// </summary>
    public static string? CurrentKey(KiteConfig config) {
        var env = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        return string.IsNullOrEmpty(env) ? config.ApiKey : env;
    }

    public static ResponsesAgent CreateDeepSeek(
        string apiKey,
        string? reasoningEffort = null,
        KiteConfig? config = null) {
        config ??= new KiteConfig();

        // The model is a layer-2 decision: env > config, and nothing else.
        var model = FirstNonEmpty(Env("KITE_MODEL"), config.Model)
                    ?? throw new InvalidOperationException(
                        $"No model configured. Set model in ~/.kite/config.json (or KITE_MODEL). Presets: {PresetIds()}; custom names are also allowed.");
        var preset = ModelCatalog.Find(model);

        // Unknown model with no explicit baseUrl is a configuration error,
        // not an excuse to guess an endpoint.
        var baseUrl = FirstNonEmpty(Env("KITE_BASE_URL"), config.BaseUrl, preset?.BaseUrl)
                      ?? throw new InvalidOperationException(
                          $"Unknown model '{model}'. Set baseUrl in ~/.kite/config.json (or KITE_BASE_URL).");

        // Instructions: only what is explicitly configured; null means
        // "no instructions" and nothing is sent. "$name" prompt references
        // belong to the app-side catalog; a user-side "$..." is an error,
        // never a literal.
        if (config.Instructions is { } configInstructions && configInstructions.StartsWith('$')) {
            throw new InvalidOperationException(
                "config.instructions does not support '$' references. Write the prompt text directly.");
        }

        var instructions = Env("KITE_INSTRUCTIONS") ?? (config.Instructions ?? preset?.Instructions);

        // Reasoning effort: explicit (/variants) > env > config; a preset-bound
        // model accepts only the variants its preset declares.
        var effort = reasoningEffort
                     ?? FirstNonEmpty(Env("KITE_REASONING"), config.Variants);
        var variants = preset?.Variants;
        if (effort is not null && variants is not null &&
            !variants.Contains(effort, StringComparer.OrdinalIgnoreCase)) {
            throw new InvalidOperationException(
                $"Model '{model}' does not support reasoning effort '{effort}'. Available: {string.Join(" / ", variants)}");
        }

        var maxOutputTokens = int.TryParse(Env("KITE_MAX_OUTPUT_TOKENS"), out var max)
            ? max
            : preset?.Limit?.Output;

        double? temperature = double.TryParse(Env("KITE_TEMPERATURE"), out var temp)
            ? temp
            : null;

        return new ResponsesAgent(
            apiKey,
            baseUrl: baseUrl,
            model: model,
            instructions: instructions,
            reasoningEffort: effort,
            maxOutputTokens: maxOutputTokens,
            temperature: temperature);
    }

    private static string? FirstNonEmpty(params string?[] values) {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string PresetIds() =>
        string.Join(", ", ModelCatalog.Presets.Select(p => p.Id));

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);
}

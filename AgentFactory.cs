using Kite.Agent;

namespace Kite;

/// <summary>
/// Single entry point for building agents: reads env vars and local config.
/// Used at startup (Program), after /connect and /variants (KiteApp).
/// Env vars take precedence over the local config.
/// </summary>
public static class AgentFactory {
    public static IAgent FromEnvOrConfig(KiteConfig? config = null) {
        config ??= new KiteConfig();
        var key = CurrentKey(config);
        return string.IsNullOrEmpty(key) ? new FakeAgent() : CreateDeepSeek(key, config.ReasoningEffort);
    }

    /// <summary>Effective API key: env var first, then the local config.</summary>
    public static string? CurrentKey(KiteConfig config) {
        var env = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        return string.IsNullOrEmpty(env) ? config.ApiKey : env;
    }

    public static ResponsesAgent CreateDeepSeek(string apiKey, string? reasoningEffort = null) => new(
        apiKey,
        baseUrl: Environment.GetEnvironmentVariable("KITE_BASE_URL") ?? ResponsesAgent.DefaultBaseUrl,
        model: Environment.GetEnvironmentVariable("KITE_MODEL") ?? ResponsesAgent.DefaultModel,
        instructions: Environment.GetEnvironmentVariable("KITE_INSTRUCTIONS") ?? ResponsesAgent.DefaultInstructions,
        reasoningEffort: reasoningEffort ?? Environment.GetEnvironmentVariable("KITE_REASONING") ?? "none",
        maxOutputTokens: int.TryParse(Environment.GetEnvironmentVariable("KITE_MAX_OUTPUT_TOKENS"), out var max)
            ? max
            : null,
        temperature: double.TryParse(Environment.GetEnvironmentVariable("KITE_TEMPERATURE"), out var temp)
            ? temp
            : null);
}
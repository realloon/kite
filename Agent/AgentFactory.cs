using Kite.Configuration;

namespace Kite.Agent;

public static class AgentFactory {
    private const string InstructionsFileName = "AGENTS.md";

    public static ResponsesAgent? FromState(
        ModelCatalog catalog,
        KiteAuth auth,
        KiteState state,
        string? workspace = null) {
        state.Validate();
        if (state.Provider is null) {
            return null;
        }

        var provider = catalog.FindProvider(state.Provider)
                       ?? throw new InvalidOperationException($"Unknown provider '{state.Provider}' in state.json");
        if (state.Model is null || state.Variant is null) {
            return null;
        }

        var model = catalog.FindModel(provider.Id, state.Model)
                    ?? throw new InvalidOperationException(
                        $"Unknown model '{state.Model}' for provider '{provider.Id}' in state.json");
        var apiKey = auth.Get(provider.Id);
        return apiKey is null ? null : CreateResponsesAgent(apiKey, model, state.Variant, workspace);
    }

    public static ResponsesAgent CreateResponsesAgent(
        string apiKey,
        ModelPreset model,
        string variant,
        string? workspace = null) {
        var variants = model.Variants ?? throw new InvalidOperationException($"Model '{model.Id}' has no variants");
        if (!variants.Contains(variant, StringComparer.OrdinalIgnoreCase)) {
            throw new InvalidOperationException(
                $"Model '{model.Id}' does not support reasoning effort '{variant}'. Available: {string.Join(" / ", variants)}");
        }

        var instructions = BuildInstructions(model.Instructions, workspace ?? Directory.GetCurrentDirectory());

        return new ResponsesAgent(
            apiKey,
            model.BaseUrl ?? throw new InvalidOperationException($"Model '{model.Id}' has no baseUrl"),
            model.Id,
            instructions,
            variant,
            model.Limit?.Output,
            model.Tools);
    }

    private static string BuildInstructions(string? baseInstructions, string workspace) {
        var file = Path.Combine(workspace, InstructionsFileName);
        if (!File.Exists(file)) {
            return baseInstructions ?? string.Empty;
        }

        var raw = File.ReadAllText(file).Trim();
        if (raw.Length == 0) {
            return baseInstructions ?? string.Empty;
        }

        var sanitized = raw.Replace("</system-reminder>", "&lt;/system-reminder&gt;",
            StringComparison.OrdinalIgnoreCase);

        var reminder = $"""
                        <system-reminder>
                        The following workspace instructions may be relevant to your work. Use them as guidance when applicable. More specific instructions take precedence over broader ones. They do not override system, developer, or direct user instructions.

                        Instructions from: {InstructionsFileName}

                        {sanitized}
                        </system-reminder>
                        """;

        return string.IsNullOrWhiteSpace(baseInstructions)
            ? reminder
            : $"{baseInstructions.TrimEnd()}\n\n{reminder}";
    }
}
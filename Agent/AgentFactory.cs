using Kite.Configuration;
using Kite.Context;

namespace Kite.Agent;

public static class AgentFactory {
    public static IAgent? FromState(ModelCatalog catalog, KiteAuth auth, KiteState state, string workspace) {
        state.Validate();
        if (state.Provider is null || state.Model is null) {
            return null;
        }

        var provider = catalog.FindProvider(state.Provider)
                       ?? throw new InvalidOperationException($"Unknown provider '{state.Provider}' in state.json");
        var model = catalog.FindModel(provider.Id, state.Model)
                    ?? throw new InvalidOperationException(
                        $"Unknown model '{state.Model}' for provider '{provider.Id}' in state.json");
        if (model.Variants.Count > 0 && state.Variant is null) {
            return null;
        }

        var apiKey = auth.Get(provider.Id);
        return apiKey is null
            ? null
            : Create(apiKey, model, state.Variant,
                ContextBuilder.Build(model.Instructions, workspace));
    }

    public static IAgent Create(string apiKey, ModelPreset model, string? variant, string? instructions) =>
        model.Api switch {
            "responses" => ResponsesAgent.Create(apiKey, model, variant, instructions),
            "completions" => CompletionsAgent.Create(apiKey, model, variant, instructions),
            _ => throw new InvalidOperationException($"Unsupported API '{model.Api}' for model '{model.Id}'")
        };
}

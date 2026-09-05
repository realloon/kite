using Kite.Configuration;

namespace Kite.Agent;

public static class AgentFactory {
    public static ResponsesAgent? FromState(ModelCatalog catalog, KiteAuth auth, KiteState state) {
        state.Validate();
        if (state.Provider is null) return null;

        var provider = catalog.FindProvider(state.Provider) ??
                       throw new InvalidOperationException($"Unknown provider '{state.Provider}' in state.json");
        if (state.Model is null || state.Variant is null) return null;

        var model = catalog.FindModel(provider.Id, state.Model)
                    ?? throw new InvalidOperationException(
                        $"Unknown model '{state.Model}' for provider '{provider.Id}' in state.json");
        var apiKey = auth.Get(provider.Id);
        return apiKey is null ? null : CreateResponsesAgent(apiKey, model, state.Variant);
    }

    public static ResponsesAgent CreateResponsesAgent(
        string apiKey,
        ModelPreset model,
        string variant) {
        var variants = model.Variants ?? throw new InvalidOperationException($"Model '{model.Id}' has no variants");
        if (!variants.Contains(variant, StringComparer.OrdinalIgnoreCase)) {
            throw new InvalidOperationException(
                $"Model '{model.Id}' does not support reasoning effort '{variant}'. Available: {string.Join(" / ", variants)}");
        }

        return new ResponsesAgent(
            apiKey,
            model.BaseUrl ?? throw new InvalidOperationException($"Model '{model.Id}' has no baseUrl"),
            model.Id,
            model.Instructions,
            variant,
            model.Limit?.Output,
            model.Tools);
    }
}
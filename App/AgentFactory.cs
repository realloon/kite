using Kite.Agent;
using Kite.Config;
using Kite.Context;
using Kite.Tools;

namespace Kite.App;

internal static class AgentFactory {
    private static readonly ToolDefinition[] LocalTools = [
        RunShell.Definition,
        SkillTool.Definition,
        .. FileTools.Definitions
    ];

    internal static AgentClient? FromState(ModelCatalog catalog, KiteAuth auth, KiteState state,
        string workspaceContext) {
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
        return apiKey is null ? null : Create(apiKey, model, state.Variant, workspaceContext);
    }

    internal static AgentClient Create(string apiKey, ModelPreset model, string? variant, string workspaceContext) {
        var instructions = ContextBuilder.Build(model.Instructions, workspaceContext);
        return model.Api switch {
            "responses" => ResponsesAgent.Create(apiKey, model, variant, instructions, LocalTools),
            "completions" => CompletionsAgent.Create(apiKey, model, variant, instructions, LocalTools),
            _ => throw new InvalidOperationException($"Unsupported API '{model.Api}' for model '{model.Id}'")
        };
    }
}
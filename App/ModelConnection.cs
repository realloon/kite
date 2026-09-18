using Kite.Agent;
using Kite.Config;
using Kite.Ui;

namespace Kite.App;

internal sealed class ModelConnection(
    FullScreenChatView view,
    ModelCatalog catalog,
    KiteAuth auth,
    KiteState state,
    string workspaceContext,
    AgentClient? agent) : IDisposable {
    public AgentClient? Agent { get; private set; } = agent;

    public async Task ConnectAsync(CancellationToken ct) {
        var provider = await SelectProviderAsync(ct);
        if (provider is null) return;

        var currentKey = auth.Get(provider.Id);
        var prompt = currentKey is null
            ? $"{provider.Id} API key (Enter to submit, Ctrl+C to cancel):"
            : $"Replace the {provider.Id} API key (Enter to keep the current key):";
        var key = await view.ReadSecretAsync(prompt, ct);
        if (key is null) {
            view.WriteInfo("Connection cancelled.");
            return;
        }

        var trimmedKey = string.IsNullOrWhiteSpace(key) ? currentKey : key.Trim();
        if (trimmedKey is null) {
            view.WriteError("No API key entered. Connection cancelled.");
            return;
        }

        auth.Set(provider.Id, trimmedKey);
        auth.Save();

        if (state.Provider is not null &&
            provider.Id.Equals(state.Provider, StringComparison.OrdinalIgnoreCase)) {
            var model = state.Model is null
                ? null
                : provider.Models.FirstOrDefault(m =>
                    m.Id.Equals(state.Model, StringComparison.OrdinalIgnoreCase));
            if (model is not null) {
                var variant = state.Variant;
                if (model.Variants.Count > 0 && variant is null) {
                    variant = await SelectVariantForModelAsync(model, ct);
                    if (variant is null) {
                        view.WriteInfo("Connection cancelled.");
                        return;
                    }

                    state.Variant = variant;
                    state.Save();
                }

                AgentClient? newAgent = null;
                try {
                    newAgent = AgentFactory.Create(trimmedKey, model, variant, workspaceContext);
                    ActivateAgent(newAgent);
                    view.WriteInfo($"Connected to {provider.Id} · {newAgent.DisplayName}. Ready.");
                    return;
                } catch (Exception ex) {
                    newAgent?.Dispose();
                    view.WriteError($"Connection refresh failed: {ex.Message}");
                    return;
                }
            }
        }

        if (state.Provider is not null) {
            view.WriteInfo($"Saved API key for {provider.Id}. Use /model to switch models.");
            return;
        }

        var selectedModel = await SelectModelForProviderAsync(provider, ct);
        if (selectedModel is null) {
            view.WriteInfo("Connection cancelled.");
            return;
        }

        string? selectedVariant = null;
        if (selectedModel.Variants.Count > 0) {
            selectedVariant = await SelectVariantForModelAsync(selectedModel, ct);
            if (selectedVariant is null) {
                view.WriteInfo("Connection cancelled.");
                return;
            }
        }

        AgentClient? agent = null;
        try {
            agent = AgentFactory.Create(trimmedKey, selectedModel, selectedVariant, workspaceContext);
            state.Provider = provider.Id;
            state.Model = selectedModel.Id;
            state.Variant = selectedVariant;
            state.Save();

            ActivateAgent(agent);
            view.WriteInfo($"Connected to {provider.Id} · {agent.DisplayName}. Ready.");
        } catch (Exception ex) {
            agent?.Dispose();
            view.WriteError($"Connection failed: {ex.Message}");
        }
    }

    private async Task<ProviderPreset?> SelectProviderAsync(CancellationToken ct) {
        if (catalog.Providers.Count == 1) {
            return catalog.Providers[0];
        }

        var choices = catalog.Providers
            .Select(p => {
                var isCurrent = p.Id.Equals(state.Provider, StringComparison.OrdinalIgnoreCase);
                return $"{(isCurrent ? "* " : "  ")}{p.Id}";
            })
            .ToArray();
        var index = await SelectIndexAsync(choices, ct);
        return index is null ? null : catalog.Providers[index.Value];
    }

    private async Task<ModelPreset?> SelectModelForProviderAsync(ProviderPreset provider, CancellationToken ct) {
        var choices = provider.Models
            .Select(model => {
                var selected = provider.Id.Equals(state.Provider, StringComparison.OrdinalIgnoreCase)
                               && model.Id.Equals(state.Model, StringComparison.OrdinalIgnoreCase);
                return $"{(selected ? "* " : "  ")}{model.Id}";
            })
            .ToArray();
        var index = await SelectIndexAsync(choices, ct);
        return index is null ? null : provider.Models[index.Value];
    }

    private async Task<string?> SelectVariantForModelAsync(ModelPreset model, CancellationToken ct) {
        if (model.Variants.Count == 0) {
            return null;
        }

        var choices = model.Variants
            .Select(variant => {
                var selected = state.Variant is not null && variant.Equals(state.Variant, StringComparison.Ordinal);
                return $"{(selected ? "* " : "  ")}{variant}";
            })
            .ToArray();
        var index = await SelectIndexAsync(choices, ct);
        return index is null ? null : model.Variants[index.Value];
    }

    private async Task<int?> SelectIndexAsync(string[] choices, CancellationToken ct) {
        var result = await view.ReadChoiceAsync(choices, ct);
        if (result is null) {
            return null;
        }

        if (result.Index < 0 || result.Index >= choices.Length) {
            throw new InvalidOperationException("The selected option no longer exists");
        }

        return result.Index;
    }

    public async Task ChangeModelAsync(CancellationToken ct) {
        var models = catalog.Models
            .Where(selection => auth.Get(selection.Provider.Id) is not null)
            .ToArray();
        if (models.Length == 0) {
            view.WriteError("No configured models. Run /connect first.");
            return;
        }

        var choices = models
            .Select(selection => {
                var selected = selection.Provider.Id.Equals(state.Provider, StringComparison.OrdinalIgnoreCase)
                               && selection.Model.Id.Equals(state.Model, StringComparison.OrdinalIgnoreCase);
                return $"{(selected ? "* " : "  ")}{selection.Provider.Id}/{selection.Model.Id}";
            })
            .ToArray();
        if (await SelectIndexAsync(choices, ct) is not { } index) return;

        var selection = models[index];
        string? variant = null;
        if (selection.Model.Variants.Count > 0) {
            if (state.Variant is { } currentVariant
                && selection.Model.Variants.Contains(currentVariant, StringComparer.Ordinal)) {
                variant = currentVariant;
            } else {
                variant = await SelectVariantForModelAsync(selection.Model, ct);
                if (variant is null) return;
            }
        }

        var key = auth.Get(selection.Provider.Id);
        if (key is null) {
            view.WriteError($"No API key for {selection.Provider.Id}. Run /connect first.");
            return;
        }

        AgentClient? newAgent = null;
        var previousProvider = state.Provider;
        var previousModel = state.Model;
        var previousVariant = state.Variant;
        try {
            newAgent = AgentFactory.Create(key, selection.Model, variant, workspaceContext);

            state.Provider = selection.Provider.Id;
            state.Model = selection.Model.Id;
            state.Variant = variant;
            state.Save();

            ActivateAgent(newAgent);
        } catch (Exception ex) {
            state.Provider = previousProvider;
            state.Model = previousModel;
            state.Variant = previousVariant;
            newAgent?.Dispose();
            view.WriteError($"Model change failed: {ex.Message}");
        }
    }

    public async Task ChangeVariantAsync(CancellationToken ct) {
        if (Agent is null) {
            view.WriteError("Run /connect first.");
            return;
        }

        var (provider, model) = RequireCurrentSelection();
        var key = auth.Get(provider.Id);
        if (key is null) {
            view.WriteError("No API key. Run /connect first.");
            return;
        }

        if (model.Variants.Count == 0) {
            view.WriteError($"No reasoning effort options available for {model.Id}.");
            return;
        }

        var value = await SelectVariantForModelAsync(model, ct);
        if (value is null) return;

        AgentClient? newAgent = null;
        var previousVariant = state.Variant;
        try {
            newAgent = AgentFactory.Create(key, model, value, workspaceContext);
            state.Variant = value;
            state.Save();

            ActivateAgent(newAgent);
        } catch (Exception ex) {
            state.Variant = previousVariant;
            newAgent?.Dispose();
            view.WriteError($"Change failed: {ex.Message}");
        }
    }

    private void ActivateAgent(AgentClient newAgent) {
        var oldAgent = Agent;
        Agent = newAgent;
        view.SetModelName(newAgent.DisplayName);
        DisposePreviousAgent(oldAgent);
    }

    private (ProviderPreset Provider, ModelPreset Model) RequireCurrentSelection() {
        state.Validate();
        if (state.Provider is null || state.Model is null) {
            throw new InvalidOperationException("state has no current model");
        }

        var provider = catalog.FindProvider(state.Provider)
                       ?? throw new InvalidOperationException($"Unknown provider '{state.Provider}' in state.json");
        var model = catalog.FindModel(provider.Id, state.Model)
                    ?? throw new InvalidOperationException(
                        $"Unknown model '{state.Model}' for provider '{provider.Id}' in state.json");
        return (provider, model);
    }

    private void DisposePreviousAgent(AgentClient? agent) {
        try {
            agent?.Dispose();
        } catch (Exception ex) {
            view.WriteError($"Previous connection cleanup failed: {ErrorMessage(ex)}");
        }
    }

    private static string ErrorMessage(Exception exception) => string.IsNullOrWhiteSpace(exception.Message)
        ? exception.GetType().Name
        : exception.Message;

    public void Dispose() {
        var agent = Agent;
        Agent = null;
        agent?.Dispose();
    }
}
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
    public ModelPreset? CurrentModel => Agent?.Model;

    public async Task ConnectAsync(CancellationToken ct) {
        var provider = catalog.Providers.Count == 1
            ? catalog.Providers[0]
            : await SelectAsync(
                catalog.Providers,
                p => p.Id,
                p => p.Id.Equals(state.Provider, StringComparison.OrdinalIgnoreCase),
                ct);
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
            provider.Id.Equals(state.Provider, StringComparison.OrdinalIgnoreCase) &&
            catalog.FindModel(provider.Id, state.Model) is { } activeModel) {
            var variant = state.Variant;
            if (activeModel.Variants.Count > 0 && variant is null) {
                variant = await SelectVariantAsync(activeModel, ct);
                if (variant is null) {
                    view.WriteInfo("Connection cancelled.");
                    return;
                }
            }

            if (TryActivate(trimmedKey, activeModel, variant, saveState: variant != state.Variant,
                    "Connection refresh failed", out var refreshed)) {
                view.WriteInfo($"Connected to {provider.Id} · {refreshed.DisplayName}. Ready.");
            }

            return;
        }

        if (state.Provider is not null) {
            view.WriteInfo($"Saved API key for {provider.Id}. Use /model to switch models.");
            return;
        }

        var selectedModel = await SelectAsync(
            provider.Models,
            m => m.Id,
            m => provider.Id.Equals(state.Provider, StringComparison.OrdinalIgnoreCase) &&
                 m.Id.Equals(state.Model, StringComparison.OrdinalIgnoreCase),
            ct);
        if (selectedModel is null) {
            view.WriteInfo("Connection cancelled.");
            return;
        }

        var selectedVariant = await SelectVariantAsync(selectedModel, ct);
        if (selectedModel.Variants.Count > 0 && selectedVariant is null) {
            view.WriteInfo("Connection cancelled.");
            return;
        }

        if (TryActivate(trimmedKey, selectedModel, selectedVariant, saveState: true, "Connection failed",
                out var connected)) {
            view.WriteInfo($"Connected to {provider.Id} · {connected.DisplayName}. Ready.");
        }
    }

    public async Task ChangeModelAsync(CancellationToken ct) {
        var models = catalog.Models
            .Where(selection => auth.Get(selection.Provider.Id) is not null)
            .ToArray();
        if (models.Length == 0) {
            view.WriteError("No configured models. Run /connect first.");
            return;
        }

        var selection = await SelectAsync(
            models,
            item => $"{item.Provider.Id}/{item.Model.Id}",
            item => item.Provider.Id.Equals(state.Provider, StringComparison.OrdinalIgnoreCase) &&
                    item.Model.Id.Equals(state.Model, StringComparison.OrdinalIgnoreCase),
            ct);
        if (selection == default) return;

        string? variant = null;
        if (selection.Model.Variants.Count > 0) {
            if (state.Variant is { } currentVariant &&
                selection.Model.Variants.Contains(currentVariant, StringComparer.Ordinal)) {
                variant = currentVariant;
            } else {
                variant = await SelectVariantAsync(selection.Model, ct);
                if (variant is null) return;
            }
        }

        var key = auth.Get(selection.Provider.Id);
        if (key is null) {
            view.WriteError($"No API key for {selection.Provider.Id}. Run /connect first.");
            return;
        }

        TryActivate(key, selection.Model, variant, saveState: true, "Model change failed", out _);
    }

    public async Task ChangeVariantAsync(CancellationToken ct) {
        if (Agent is null) {
            view.WriteError("Run /connect first.");
            return;
        }

        var (provider, model) = catalog.ResolveSelection(state);
        var key = auth.Get(provider.Id);
        if (key is null) {
            view.WriteError("No API key. Run /connect first.");
            return;
        }

        if (model.Variants.Count == 0) {
            view.WriteError($"No reasoning effort options available for {model.Id}.");
            return;
        }

        var value = await SelectVariantAsync(model, ct);
        if (value is null) return;

        TryActivate(key, model, value, saveState: true, "Change failed", out _);
    }

    private Task<string?> SelectVariantAsync(ModelPreset model, CancellationToken ct) =>
        model.Variants.Count == 0
            ? Task.FromResult<string?>(null)
            : SelectAsync(
                model.Variants,
                v => v,
                v => v.Equals(state.Variant, StringComparison.Ordinal),
                ct);

    private async Task<T?> SelectAsync<T>(
        IReadOnlyList<T> items,
        Func<T, string> label,
        Func<T, bool> isSelected,
        CancellationToken ct) {
        var choices = items
            .Select(item => $"{(isSelected(item) ? "* " : "  ")}{label(item)}")
            .ToArray();
        var result = await view.ReadChoiceAsync(choices, ct);
        if (result is null) {
            return default;
        }

        if (result.Index < 0 || result.Index >= items.Count) {
            throw new InvalidOperationException("The selected option no longer exists");
        }

        return items[result.Index];
    }

    private bool TryActivate(
        string apiKey,
        ModelPreset model,
        string? variant,
        bool saveState,
        string errorPrefix,
        out AgentClient activated) {
        AgentClient? newAgent = null;
        var previous = (state.Provider, state.Model, state.Variant);
        try {
            newAgent = AgentFactory.Create(apiKey, model, variant, workspaceContext);
            if (saveState) {
                state.Provider = model.ProviderId;
                state.Model = model.Id;
                state.Variant = variant;
                state.Save();
            }

            var oldAgent = Agent;
            Agent = newAgent;
            view.SetModelName(newAgent.DisplayName);
            DisposePreviousAgent(oldAgent);
            activated = newAgent;
            return true;
        } catch (Exception ex) {
            (state.Provider, state.Model, state.Variant) = previous;
            newAgent?.Dispose();
            view.WriteError($"{errorPrefix}: {KiteApp.ErrorMessage(ex)}");
            activated = null!;
            return false;
        }
    }

    private void DisposePreviousAgent(AgentClient? previous) {
        try {
            previous?.Dispose();
        } catch (Exception ex) {
            view.WriteError($"Previous connection cleanup failed: {KiteApp.ErrorMessage(ex)}");
        }
    }

    public void Dispose() {
        var current = Agent;
        Agent = null;
        current?.Dispose();
    }
}
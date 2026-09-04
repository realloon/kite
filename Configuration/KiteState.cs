namespace Kite.Configuration;

/// <summary>Current provider/model/variant selection, separate from credentials and presets.</summary>
public sealed class KiteState {
    public string? Provider { get; set; }

    public string? Model { get; set; }

    public string? Variant { get; set; }

    public static string Path => System.IO.Path.Combine(KiteConfig.DataDirectory, "state.json");

    public static KiteState Load() {
        var state = JsonFile.Load(Path, KiteJsonContext.Default.KiteState, "state");
        state.Validate();
        return state;
    }

    public void Validate() {
        if (Provider is null) {
            if (Model is not null || Variant is not null) {
                throw new InvalidOperationException("State cannot set model or variant without a provider");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(Provider)) {
            throw new InvalidOperationException("State contains an empty provider");
        }

        if (Model is null) {
            if (Variant is not null) {
                throw new InvalidOperationException("State cannot set a variant without a model");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(Model) ||
            (Variant is not null && string.IsNullOrWhiteSpace(Variant))) {
            throw new InvalidOperationException("State contains an empty model or variant");
        }
    }

    public void Save() {
        Validate();
        JsonFile.Save(Path, this, KiteJsonContext.Default.KiteState, "state");
    }
}
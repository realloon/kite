namespace Kite.Configuration;

/// <summary>
/// Local preset layer (~/.kite/config.json) — configuration layer 2.
/// Layer 1 is the built-in preset catalog (ModelCatalog, embedded in the
/// binary); entries here override matching providers/models or add new ones.
/// </summary>
public sealed class UserPresets {
    public List<ProviderPreset> Providers { get; set; } = [];

    public static UserPresets Load() => JsonFile.Load(Paths.Config, KiteJsonContext.Default.UserPresets, "config");
}
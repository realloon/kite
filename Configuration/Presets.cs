namespace Kite.Configuration;

public sealed class Presets {
    public List<ProviderPreset> Providers { get; set; } = [];

    public static Presets Load() => JsonFile.Load(Paths.Presets, KiteJsonContext.Default.Presets, "presets");
}
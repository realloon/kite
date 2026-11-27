namespace Kite.Config;

internal sealed class Presets {
    public List<RawProviderPreset> Providers { get; set; } = [];

    public static Presets Load() => JsonFile.Load(Paths.Presets, ConfigJsonContext.Default.Presets, "presets");
}
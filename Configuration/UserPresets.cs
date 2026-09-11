namespace Kite.Configuration;

public sealed class UserPresets {
    public List<ProviderPreset> Providers { get; set; } = [];

    public static UserPresets Load() => JsonFile.Load(Paths.Config, KiteJsonContext.Default.UserPresets, "config");
}
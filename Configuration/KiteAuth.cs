namespace Kite.Configuration;

/// <summary>Provider credentials stored separately from presets and app state.</summary>
public sealed class KiteAuth {
    public Dictionary<string, string> ApiKeys { get; set; } = [];

    public static string Path => System.IO.Path.Combine(KiteConfig.DataDirectory, "auth.json");

    public static KiteAuth Load() {
        var auth = JsonFile.Load(Path, KiteJsonContext.Default.KiteAuth, "auth");
        auth.Validate();
        return auth;
    }

    public string? Get(string providerId) => ApiKeys
        .FirstOrDefault(pair => string.Equals(pair.Key, providerId, StringComparison.OrdinalIgnoreCase))
        .Value;

    public void Set(string providerId, string apiKey) {
        var existing = ApiKeys.Keys.FirstOrDefault(key =>
            string.Equals(key, providerId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) ApiKeys.Remove(existing);
        ApiKeys[providerId] = apiKey;
    }

    public void Remove(string providerId) {
        var existing = ApiKeys.Keys.FirstOrDefault(key =>
            string.Equals(key, providerId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) ApiKeys.Remove(existing);
    }

    public void Save() {
        Validate();
        JsonFile.Save(Path, this, KiteJsonContext.Default.KiteAuth, "auth");
    }

    private void Validate() {
        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in ApiKeys) {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value) ||
                !providers.Add(pair.Key)) {
                throw new InvalidOperationException("Auth file contains an invalid or duplicate provider key");
            }
        }
    }
}
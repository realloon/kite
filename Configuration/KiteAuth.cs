namespace Kite.Configuration;

public sealed class KiteAuth {
    public Dictionary<string, string> ApiKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string Path => System.IO.Path.Combine(Paths.DataDirectory, "auth.json");

    public static KiteAuth Load() {
        var auth = JsonFile.Load(Path, KiteJsonContext.Default.KiteAuth, "auth");
        auth.ApiKeys = new Dictionary<string, string>(auth.ApiKeys, StringComparer.OrdinalIgnoreCase);
        auth.Validate();
        return auth;
    }

    public string? Get(string providerId) => ApiKeys.GetValueOrDefault(providerId);

    public void Set(string providerId, string apiKey) {
        ApiKeys[providerId] = apiKey;
    }

    public void Save() {
        Validate();
        JsonFile.Save(Path, this, KiteJsonContext.Default.KiteAuth, "auth");
    }

    private void Validate() {
        foreach (var pair in ApiKeys) {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) {
                throw new InvalidOperationException("Auth file contains an invalid or duplicate provider key");
            }
        }
    }
}
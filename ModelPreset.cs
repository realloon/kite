using System.Text.Json.Serialization;

namespace Kite;

/// <summary>
/// One provider (configuration layer 1, embedded in the binary): its models
/// share the same baseUrl. Users pick a model through layer 2
/// (~/.kite/config.json and KITE_* env vars, see AgentFactory); the catalog
/// itself never decides which model is used.
/// </summary>
public sealed class ProviderPreset {
    public string Id { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public List<ModelPreset>? Models { get; set; }
}

/// <summary>Token limits: context window and max output. Both are mandatory for every preset.</summary>
public sealed class ModelLimit {
    /// <summary>Context window in tokens. Recorded only — no truncation or window warning logic yet.</summary>
    public int? Context { get; set; }

    public int? Output { get; set; }
}

/// <summary>
/// One model preset: identity plus token limits, instructions, available
/// variants and cost. BaseUrl is stitched in from the owning provider at
/// load time. Any field left unset fails validation at startup
/// (see ModelCatalog).
/// </summary>
public sealed class ModelPreset {
    /// <summary>Model id; sent to the API, accepted in config.model and shown in the UI.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Inherited from the owning provider; filled at load time.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public ModelLimit? Limit { get; set; }

    /// <summary>Default instructions; used when the user config sets none.</summary>
    public string? Instructions { get; set; }

    /// <summary>Variants this model accepts; /variants picks from this list.</summary>
    public List<string>? Variants { get; set; }

    /// <summary>Recorded only — no cost display or accounting yet.</summary>
    public ModelCost? Cost { get; set; }
}

/// <summary>
/// Per-1M-token prices; all four fields are mandatory — presets.json
/// validation rejects any preset with a missing one. DeepSeek-style split:
/// input is the cache-miss rate, cache_write the miss (write) rate,
/// cache_read the hit (read) rate. Recorded only — no cost display or
/// accounting yet.
/// </summary>
public sealed class ModelCost {
    public double? Input { get; set; }

    public double? Output { get; set; }

    /// <summary>Cache-miss input rate (cache write).</summary>
    [JsonPropertyName("cache_write")]
    public double? CacheWrite { get; set; }

    /// <summary>Cache-hit input rate (cache read).</summary>
    [JsonPropertyName("cache_read")]
    public double? CacheRead { get; set; }
}

/// <summary>
/// Root object of the embedded presets.json. The catalog's single source of
/// truth: no code-side defaults exist, so a missing or corrupt resource is a
/// build/packaging error and fails loudly at startup.
/// </summary>
internal sealed class PresetFile {
    public List<ProviderPreset>? Providers { get; set; }
}
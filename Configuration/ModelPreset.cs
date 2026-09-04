using System.Text.Json.Serialization;

namespace Kite.Configuration;

/// <summary>
/// One provider preset: its models share the same baseUrl. The built-in
/// catalog and user config use this same shape; the current selection lives
/// in state.json.
/// </summary>
public sealed class ProviderPreset {
    public string Id { get; set; } = string.Empty;

    public string? BaseUrl { get; set; }

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
    public string Id { get; set; } = string.Empty;

    /// <summary>Optional provider endpoint override; filled with the effective endpoint at catalog load time.</summary>
    public string? BaseUrl { get; set; }

    public ModelLimit? Limit { get; set; }

    /// <summary>Default instructions; used when the user config sets none.</summary>
    public string? Instructions { get; set; }

    /// <summary>Variants this model accepts; /variants picks from this list.</summary>
    public List<string>? Variants { get; set; }

    /// <summary>Per-million-token prices for each billing period.</summary>
    public ModelCost? Cost { get; set; }
}

/// <summary>
/// Per-million-token prices for peak and off-peak periods.
/// </summary>
public sealed class ModelCost {
    public string Currency { get; set; } = string.Empty;

    public ModelPrice? Peak { get; set; }

    [JsonPropertyName("off_peak")]
    public ModelPrice? OffPeak { get; set; }
}

public sealed class ModelPrice {
    [JsonPropertyName("input")]
    public double? Input { get; set; }

    [JsonPropertyName("output")]
    public double? Output { get; set; }

    [JsonPropertyName("cache_write")]
    public double? CacheWrite { get; set; }

    [JsonPropertyName("cache_read")]
    public double? CacheRead { get; set; }
}
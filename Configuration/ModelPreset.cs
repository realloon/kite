using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kite.Configuration;

public sealed class ProviderPreset {
    public string Id { get; set; } = string.Empty;

    public string? BaseUrl { get; set; }

    public List<ModelPreset> Models { get; set; } = [];
}

public sealed class ModelLimit {
    /// <summary>Context window in tokens. Recorded only — no truncation or window warning logic yet.</summary>
    public int? Context { get; set; }

    public int? Output { get; set; }
}

public sealed class ModelPreset {
    public string Id { get; set; } = string.Empty;

    [JsonIgnore]
    public string ProviderId { get; set; } = string.Empty;

    public string? BaseUrl { get; set; }

    public ModelLimit? Limit { get; set; }

    public string Instructions { get; set; } = string.Empty;

    public List<string> Variants { get; set; } = [];

    public List<JsonElement> Tools { get; set; } = [];

    public ModelCost? Cost { get; set; }
}

public sealed class ModelCost {
    public string Currency { get; set; } = "$";

    [JsonPropertyName("input")]
    public double? Input { get; set; }

    [JsonPropertyName("output")]
    public double? Output { get; set; }

    [JsonPropertyName("cache_write")]
    public double CacheWrite { get; set; }

    [JsonPropertyName("cache_read")]
    public double? CacheRead { get; set; }

    public ModelPrice? Peak { get; set; }

    [JsonPropertyName("off_peak")]
    public ModelPrice? OffPeak { get; set; }

    public ModelPrice? CurrentPrice() {
        if (Peak is null) {
            return null;
        }

        if (OffPeak is null) {
            return Peak;
        }

        var cst = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));
        var isWorkday = cst.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday;
        var time = cst.TimeOfDay;
        var isPeakTime = (time >= new TimeSpan(9, 0, 0) && time < new TimeSpan(12, 0, 0)) ||
                         (time >= new TimeSpan(14, 0, 0) && time < new TimeSpan(18, 0, 0));

        return isWorkday && isPeakTime ? Peak : OffPeak;
    }
}

public sealed class ModelPrice {
    [JsonPropertyName("input")]
    public double? Input { get; set; }

    [JsonPropertyName("output")]
    public double? Output { get; set; }

    [JsonPropertyName("cache_write")]
    public double CacheWrite { get; set; }

    [JsonPropertyName("cache_read")]
    public double? CacheRead { get; set; }
}
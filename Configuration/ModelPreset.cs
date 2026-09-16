using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kite.Configuration;

public sealed class ProviderPreset {
    public string Id { get; init; } = string.Empty;

    public string? BaseUrl { get; set; }

    public List<ModelPreset> Models { get; init; } = [];
}

public sealed class ModelLimit {
    public int Context { get; init; } = 32_768;

    public int Output { get; init; } = 4_096;
}

public sealed class ModelPreset {
    public string Id { get; init; } = string.Empty;

    public string Api { get; set; } = "responses";

    [JsonIgnore]
    public string ProviderId { get; set; } = string.Empty;

    public string? BaseUrl { get; set; }

    public ModelLimit Limit { get; init; } = new();

    public string Instructions { get; set; } = string.Empty;

    public List<string> Variants { get; init; } = [];

    public List<JsonElement> Tools { get; init; } = [];

    public ModelCost? Cost { get; init; }
}

public sealed class ModelCost {
    public string Currency { get; set; } = "$";

    [JsonPropertyName("input")]
    public double? Input { get; init; }

    [JsonPropertyName("output")]
    public double? Output { get; init; }

    [JsonPropertyName("cache_write")]
    public double CacheWrite { get; init; }

    [JsonPropertyName("cache_read")]
    public double? CacheRead { get; init; }

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
    public double? Input { get; init; }

    [JsonPropertyName("output")]
    public double? Output { get; init; }

    [JsonPropertyName("cache_write")]
    public double CacheWrite { get; init; }

    [JsonPropertyName("cache_read")]
    public double? CacheRead { get; init; }
}
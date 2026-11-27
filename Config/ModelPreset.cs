using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kite.Config;

internal enum ModelApi {
    Responses,
    Completions
}

internal sealed record ProviderPreset(string Id, string BaseUrl, IReadOnlyList<ModelPreset> Models);

internal sealed record ModelLimit(int Context, int Output);

internal sealed record ModelPrice(double Input, double Output, double CacheRead, double CacheWrite = 0);

internal sealed record ModelCost(string Currency, ModelPrice Peak, ModelPrice OffPeak) {
    public ModelPrice CurrentPrice() {
        var cst = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));
        var isWorkday = cst.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday;
        var time = cst.TimeOfDay;
        var isPeakTime = (time >= new TimeSpan(9, 0, 0) && time < new TimeSpan(12, 0, 0)) ||
                         (time >= new TimeSpan(14, 0, 0) && time < new TimeSpan(18, 0, 0));
        return isWorkday && isPeakTime ? Peak : OffPeak;
    }
}

internal sealed record ModelPreset(
    string Id,
    ModelApi Api,
    string ProviderId,
    string BaseUrl,
    ModelLimit Limit,
    string Instructions,
    IReadOnlyList<string> Variants,
    IReadOnlyList<JsonElement> Tools,
    ModelCost Cost);

internal sealed class RawProviderPreset {
    public string? Id { get; set; }
    public string? BaseUrl { get; set; }
    public List<RawModelPreset>? Models { get; set; }
}

internal sealed class RawModelLimit {
    public int? Context { get; set; }
    public int? Output { get; set; }
}

internal sealed class RawModelPreset {
    public string? Id { get; set; }
    public string? Api { get; set; }
    public string? BaseUrl { get; set; }
    public RawModelLimit? Limit { get; set; }
    public string? Instructions { get; set; }
    public List<string>? Variants { get; set; }
    public List<JsonElement>? Tools { get; set; }
    public RawModelCost? Cost { get; set; }
}

internal sealed class RawModelCost {
    public string? Currency { get; set; }
    public double? Input { get; set; }
    public double? Output { get; set; }

    [JsonPropertyName("cache_write")]
    public double? CacheWrite { get; set; }

    [JsonPropertyName("cache_read")]
    public double? CacheRead { get; set; }

    public RawModelPrice? Peak { get; set; }

    [JsonPropertyName("off_peak")]
    public RawModelPrice? OffPeak { get; set; }
}

internal sealed class RawModelPrice {
    public double? Input { get; set; }
    public double? Output { get; set; }

    [JsonPropertyName("cache_write")]
    public double? CacheWrite { get; set; }

    [JsonPropertyName("cache_read")]
    public double? CacheRead { get; set; }
}
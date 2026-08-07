using Newtonsoft.Json;

namespace Finance.Net.Models.Yahoo.Dtos;

internal record ChartSplit
{
    [JsonProperty("date")]
    public long Date { get; set; }

    [JsonProperty("numerator")]
    public double? Numerator { get; set; }

    [JsonProperty("denominator")]
    public double? Denominator { get; set; }

    [JsonProperty("splitRatio")]
    public string? SplitRatio { get; set; }
}

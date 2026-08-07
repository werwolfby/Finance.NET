using System.Collections.Generic;
using Newtonsoft.Json;

namespace Finance.Net.Models.Yahoo.Dtos;

internal record ChartResult
{
    [JsonProperty("meta")]
    public ChartMeta? Meta { get; set; }

    [JsonProperty("timestamp")]
    public List<long>? Timestamp { get; set; }

    [JsonProperty("indicators")]
    public ChartIndicators? Indicators { get; set; }

    [JsonProperty("events")]
    public ChartEvents? Events { get; set; }
}

using Newtonsoft.Json;

namespace Finance.Net.Models.Yahoo.Dtos;

internal record ChartMeta
{
    [JsonProperty("symbol")]
    public string? Symbol { get; set; }

    /// <summary>
    /// Offset of the exchange timezone from UTC, in seconds.
    /// </summary>
    [JsonProperty("gmtoffset")]
    public long GmtOffset { get; set; }

    [JsonProperty("timezone")]
    public string? Timezone { get; set; }

    /// <summary>
    /// When the latest trade happened, as a Unix timestamp.
    /// </summary>
    [JsonProperty("regularMarketTime")]
    public long? RegularMarketTime { get; set; }

    /// <summary>
    /// The price of the latest trade - the close, once the session has ended.
    /// </summary>
    [JsonProperty("regularMarketPrice")]
    public double? RegularMarketPrice { get; set; }
}

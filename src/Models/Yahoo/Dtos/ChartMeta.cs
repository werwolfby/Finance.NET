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
}

using System.Collections.Generic;
using Newtonsoft.Json;

namespace Finance.Net.Models.Yahoo.Dtos;

internal record ChartEvents
{
    /// <summary>Keyed by the event's unix timestamp, which repeats the value's own date.</summary>
    [JsonProperty("dividends")]
    public Dictionary<string, ChartDividend>? Dividends { get; set; }

    [JsonProperty("splits")]
    public Dictionary<string, ChartSplit>? Splits { get; set; }
}

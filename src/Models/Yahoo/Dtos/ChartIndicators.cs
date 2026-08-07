using System.Collections.Generic;
using Newtonsoft.Json;

namespace Finance.Net.Models.Yahoo.Dtos;

internal record ChartIndicators
{
    [JsonProperty("quote")]
    public List<ChartQuote>? Quote { get; set; }

    [JsonProperty("adjclose")]
    public List<ChartAdjClose>? AdjClose { get; set; }
}

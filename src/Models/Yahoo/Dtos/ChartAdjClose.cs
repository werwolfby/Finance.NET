using System.Collections.Generic;
using Newtonsoft.Json;

namespace Finance.Net.Models.Yahoo.Dtos;

internal record ChartAdjClose
{
    [JsonProperty("adjclose")]
    public List<double?>? AdjClose { get; set; }
}

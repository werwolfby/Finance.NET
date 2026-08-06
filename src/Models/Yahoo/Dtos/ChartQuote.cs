using System.Collections.Generic;
using Newtonsoft.Json;

namespace Finance.Net.Models.Yahoo.Dtos;

internal record ChartQuote
{
    [JsonProperty("open")]
    public List<double?>? Open { get; set; }

    [JsonProperty("high")]
    public List<double?>? High { get; set; }

    [JsonProperty("low")]
    public List<double?>? Low { get; set; }

    [JsonProperty("close")]
    public List<double?>? Close { get; set; }

    [JsonProperty("volume")]
    public List<long?>? Volume { get; set; }
}

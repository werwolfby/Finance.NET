using Newtonsoft.Json;

namespace Finance.Net.Models.Yahoo.Dtos;

internal record ChartResponseRoot
{
    [JsonProperty("chart")]
    public ChartResponse? Chart { get; set; }
}

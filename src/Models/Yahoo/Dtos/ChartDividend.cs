using Newtonsoft.Json;

namespace Finance.Net.Models.Yahoo.Dtos;

internal record ChartDividend
{
    [JsonProperty("amount")]
    public double? Amount { get; set; }

    [JsonProperty("date")]
    public long Date { get; set; }
}

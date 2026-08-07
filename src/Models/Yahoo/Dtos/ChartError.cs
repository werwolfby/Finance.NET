using Newtonsoft.Json;

namespace Finance.Net.Models.Yahoo.Dtos;

internal record ChartError
{
    [JsonProperty("code")]
    public string? Code { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }
}

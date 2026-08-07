using System.Collections.Generic;
using Newtonsoft.Json;

namespace Finance.Net.Models.Yahoo.Dtos;

internal record ChartResponse
{
    [JsonProperty("result")]
    public List<ChartResult>? Result { get; set; }

    [JsonProperty("error")]
    public ChartError? Error { get; set; }
}

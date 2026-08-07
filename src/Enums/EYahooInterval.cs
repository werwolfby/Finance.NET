using System.ComponentModel;

namespace Finance.Net.Enums;

/// <summary>
/// Represents the non-intraday granularities Yahoo! Finance serves historical records at.
/// </summary>
/// <remarks>
/// Separate from <see cref="EInterval"/>, which carries the intraday granularities shared
/// with Alpha Vantage. Unlike the intraday ones, these are not subject to a retention
/// window - they reach back to the instrument's first trading day.
/// </remarks>
public enum EYahooInterval
{
    /// <summary>
    /// One record per trading day.
    /// </summary>
    [Description("1d")]
    Daily = 1,

    /// <summary>
    /// One record per week.
    /// </summary>
    [Description("1wk")]
    Weekly = 2,

    /// <summary>
    /// One record per month.
    /// </summary>
    [Description("1mo")]
    Monthly = 3
}

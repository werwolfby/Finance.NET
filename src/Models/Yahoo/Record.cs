using System;

namespace Finance.Net.Models.Yahoo;

/// <summary>
/// Represents a historical record of stock market data.
/// </summary>
public record Record
{
    /// <summary>
    /// The date of the record.
    /// </summary>
    public DateTime Date { get; set; }

    /// <summary>
    /// The opening pricee.
    /// </summary>
    public decimal? Open { get; set; }

    /// <summary>
    /// The highest price.
    /// </summary>
    public decimal? High { get; set; }

    /// <summary>
    /// The lowest price.
    /// </summary>
    public decimal? Low { get; set; }

    /// <summary>
    /// The closing price.
    /// </summary>
    public decimal? Close { get; set; }

    /// <summary>
    /// The adjusted closing price, accounting for stock splits and dividends.
    /// </summary>
    public decimal? AdjustedClose { get; set; }

    /// <summary>
    /// The trading volume.
    /// </summary>
    public long? Volume { get; set; }

    /// <summary>
    /// The cash dividend that went ex on this date, or <c>null</c> if none did. For a weekly or
    /// monthly record, the total of those that went ex during its week or month.
    /// </summary>
    public decimal? Dividend { get; set; }

    /// <summary>
    /// The ratio of a stock split effective on this date - 10 for a 10:1 split - or
    /// <c>null</c> if no split occurred. For a weekly or monthly record, the combined ratio of
    /// the splits during its week or month - 6 for a 2:1 followed by a 3:1.
    /// </summary>
    public decimal? SplitCoefficient { get; set; }
}


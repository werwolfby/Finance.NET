using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Html.Dom;
using Finance.Net.Enums;
using Finance.Net.Exceptions;
using Finance.Net.Interfaces;
using Finance.Net.Mappings;
using Finance.Net.Models.Yahoo;
using Finance.Net.Models.Yahoo.Dtos;
using Finance.Net.Utilities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Polly;
using Polly.Registry;

namespace Finance.Net.Services;

/// <inheritdoc />
public class YahooFinanceService : IYahooFinanceService
{
    private readonly ILogger<YahooFinanceService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IYahooSessionManager _yahooSession;
    private readonly AsyncPolicy _retryPolicy;

    /// <inheritdoc />
    public YahooFinanceService(
        ILogger<YahooFinanceService> logger,
        IHttpClientFactory httpClientFactory,
        IReadOnlyPolicyRegistry<string> policyRegistry,
        IYahooSessionManager yahooSession)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _yahooSession = yahooSession ?? throw new ArgumentNullException(nameof(yahooSession));
        _retryPolicy = policyRegistry?.Get<AsyncPolicy>(Constants.DefaultHttpRetryPolicy) ?? throw new ArgumentNullException(nameof(policyRegistry));
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Instrument>> GetInstrumentsAsync(EInstrumentType? filterByType = null, CancellationToken token = default)
    {
        var result = new List<Instrument>();
        var instrumentTypes = Enum.GetValues(typeof(EInstrumentType))
                                         .Cast<EInstrumentType>()
                                         .ToList();

        var typesToProcess = filterByType == null ? instrumentTypes : [filterByType.Value];
        Exception? lastFailure = null;

        foreach (var instrumentType in typesToProcess)
        {
            try
            {
                var instruments = await (instrumentType switch
                {
                    EInstrumentType.ETF => FetchSymbolsAsync($"{Constants.YahooBaseUrl}/markets/etfs/most-active/", EInstrumentType.ETF, token),
                    EInstrumentType.Stock => FetchSymbolsAsync($"{Constants.YahooBaseUrl}/markets/stocks/most-active/", EInstrumentType.Stock, token),
                    EInstrumentType.Forex => FetchSymbolsAsync($"{Constants.YahooBaseUrl}/markets/currencies/", EInstrumentType.Forex, token),
                    EInstrumentType.Crypto => FetchSymbolsAsync($"{Constants.YahooBaseUrl}/markets/crypto/all/", EInstrumentType.Crypto, token),
                    EInstrumentType.Index => FetchSymbolsAsync($"{Constants.YahooBaseUrl}/markets/world-indices/", EInstrumentType.Index, token),
                    _ => throw new NotSupportedException()
                }).ConfigureAwait(false);
                result.AddRange(instruments);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (FinanceNetNoDataException ex)
            {
                _logger.LogWarning(ex, "No data for {Type}", instrumentType);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load {Type}", instrumentType);
                lastFailure = ex;
            }
        }
        if (!result.IsNullOrEmpty())
        {
            return result;
        }
        // A transport failure is not a permanent no-data answer - reporting it as one would tell
        // the caller to stop asking while Yahoo is merely down.
        throw lastFailure is null
            ? new FinanceNetNoDataException("No instruments found")
            : new FinanceNetException("No instruments found", lastFailure);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<IntradayRecord>> GetIntradayRecordsAsync(string symbol, DateTime startDate, DateTime? endDate = null, EInterval interval = EInterval.Interval_15Min, CancellationToken token = default)
    {
        endDate ??= DateTime.UtcNow.Date;
        if (endDate.Value.Date > DateTime.UtcNow.Date)
        {
            endDate = DateTime.UtcNow.Date;
        }
        // checked once endDate is settled, so omitting it cannot skip the check
        if (startDate.Date > endDate.Value.Date)
        {
            throw new FinanceNetException("startDate must not be later than endDate");
        }

        var limits = GetChartLimits(interval);

        // Yahoo measures its retention window from the current instant, so the boundary date taken
        // at midnight already sits marginally outside it - stay a day inside to avoid a 422.
        var earliestAvailable = DateTime.UtcNow.Date.AddDays(-(limits.RetentionDays - 1));
        if (endDate.Value.Date < earliestAvailable)
        {
            throw new FinanceNetInvalidRequestException(
                $"Yahoo keeps only the last {limits.RetentionDays} days of {ToYahooInterval(interval)} data for {symbol}, " +
                $"but the requested range ends on {endDate.Value:yyyy-MM-dd}. Request a more recent range or a wider interval.");
        }

        var effectiveStart = startDate.Date;
        if (effectiveStart < earliestAvailable)
        {
            _logger.LogWarning(
                "Yahoo keeps only the last {RetentionDays} days of {Interval} data, truncating the requested start {Requested:yyyy-MM-dd} to {Effective:yyyy-MM-dd}.",
                limits.RetentionDays,
                ToYahooInterval(interval),
                startDate.Date,
                earliestAvailable);
            effectiveStart = earliestAvailable;
        }

        await _yahooSession.RefreshSessionAsync(token).ConfigureAwait(false);

        // Yahoo caps how much intraday data a single request may span, so walk the range in chunks
        var records = new List<IntradayRecord>();
        var endExclusive = endDate.Value.Date.AddDays(1);
        for (var chunkStart = effectiveStart; chunkStart < endExclusive; chunkStart = chunkStart.AddDays(limits.MaxSpanDays))
        {
            var chunkEndExclusive = chunkStart.AddDays(limits.MaxSpanDays);
            if (chunkEndExclusive > endExclusive)
            {
                chunkEndExclusive = endExclusive;
            }
            var chunk = await GetIntradayRecordsChunkAsync(symbol, chunkStart, chunkEndExclusive, earliestAvailable, interval, token).ConfigureAwait(false);
            records.AddRange(chunk);
        }

        // newest first, matching GetRecordsAsync and the Alpha Vantage records
        records.Reverse();
        return records.IsNullOrEmpty()
            ? throw new FinanceNetNoDataException($"Yahoo returned no intraday records for {symbol}")
            : records;
    }

    private async Task<List<IntradayRecord>> GetIntradayRecordsChunkAsync(string symbol, DateTime startDate, DateTime endExclusive, DateTime earliestAvailable, EInterval interval, CancellationToken token)
    {
        var httpClient = _httpClientFactory.CreateClient(Constants.YahooHttpClientName);
        // The padding must not reach past the retention boundary, or Yahoo rejects the whole
        // request. Bars stamped before that boundary are ones this service already treats as
        // too close to Yahoo's cut-off to ask for.
        var requestStart = startDate - RequestPadding;
        if (requestStart < earliestAvailable)
        {
            requestStart = earliestAvailable;
        }
        var url = $"{Constants.YahooChartApiUrl}/{symbol}" +
            $"?interval={ToYahooInterval(interval)}" +
            $"&period1={Helper.ToUnixTime(requestStart)}" +
            $"&period2={Helper.ToUnixTime(endExclusive + RequestPadding)}";
        try
        {
            return await _retryPolicy.ExecuteAsync(async ct =>
            {
                var jsonContent = await FetchChartJsonAsync(httpClient, symbol, url, ct).ConfigureAwait(false);
                var parsedData = JsonConvert.DeserializeObject<ChartResponseRoot>(jsonContent) ?? throw new FinanceNetException("Invalid data returned by Yahoo");
                if (parsedData.Chart?.Error != null)
                {
                    throw new FinanceNetException($"Received an error response from Yahoo: {parsedData.Chart.Error}");
                }

                // a quiet chunk is not fatal - a neighbouring one may still carry records
                var chartResult = parsedData.Chart?.Result?.FirstOrDefault();
                return chartResult == null
                    ? []
                    // filter against this chunk, so the trailing bar Yahoo appends cannot be added once per chunk
                    : ParseIntradayRecords(chartResult, startDate, endExclusive.AddDays(-1));
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not (FinanceNetNoDataException or FinanceNetInvalidRequestException))
        {
            throw new FinanceNetException($"No intraday records found for {symbol}", ex);
        }
    }

    /// <summary>
    /// Fetches the chart payload. A rejection that is Yahoo's final answer about this request
    /// becomes an exception the retry policy leaves alone, carrying Yahoo's own explanation.
    /// </summary>
    /// <remarks>
    /// Only the statuses Yahoo uses to describe the request itself are final: 404 for a symbol
    /// it does not know, 400 for invalid input, 422 for a range outside what it keeps. Anything
    /// else - rate limiting, timeouts, an expired session, a server error - can clear up and
    /// stays retryable.
    /// </remarks>
    private async Task<string> FetchChartJsonAsync(HttpClient httpClient, string symbol, string url, CancellationToken token)
    {
        var response = await httpClient.GetAsync(url, token).ConfigureAwait(false);
        var jsonContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            var description = TryReadChartErrorDescription(jsonContent) ?? jsonContent;
            throw response.StatusCode == HttpStatusCode.NotFound
                ? new FinanceNetNoDataException($"Yahoo has no data for {symbol}: {description}")
                : new FinanceNetInvalidRequestException($"Yahoo rejected the request for {symbol}: {description}");
        }
        response.EnsureSuccessStatusCode();

        _logger?.LogDebug("jsonContent={JsonContent}", jsonContent.Minify());
        return jsonContent;
    }

    private static string? TryReadChartErrorDescription(string jsonContent)
    {
        try
        {
            return JsonConvert.DeserializeObject<ChartResponseRoot>(jsonContent)?.Chart?.Error?.Description;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// How far every chart request reaches past the dates it is for, on both sides.
    /// </summary>
    /// <remarks>
    /// Request bounds are UTC instants, but records belong to the exchange's local date, and a
    /// local day starts up to 14 hours before UTC midnight (UTC+14) and ends up to 12 hours after
    /// it (UTC-12). Bounds cut at UTC midnight therefore miss one edge of the day - the opening
    /// bars in New Zealand, the evening session of a New York future. Padding the request and
    /// then filtering on the local date keeps every day whole. The filter also gives each bar to
    /// exactly one local date, so the overlap between adjacent padded chunks never duplicates one.
    /// </remarks>
    private static readonly TimeSpan RequestPadding = TimeSpan.FromHours(14);

    /// <summary>
    /// How much intraday history Yahoo keeps per interval, and how much of it one request may span.
    /// </summary>
    /// <remarks>
    /// These are two different limits. 1m data is capped per request ("only 8 days worth of 1m
    /// granularity data are allowed to be fetched per request"), which chunking works around.
    /// The retention window is how far back the data exists at all, which chunking cannot extend.
    /// </remarks>
    private static (int MaxSpanDays, int RetentionDays) GetChartLimits(EInterval interval) => interval switch
    {
        // 8 days are allowed per request, padding included: 6 days plus RequestPadding on both
        // sides comes to a little over 7
        EInterval.Interval_1Min => (6, 30),
        EInterval.Interval_5Min => (60, 60),
        EInterval.Interval_15Min => (60, 60),
        EInterval.Interval_30Min => (60, 60),
        EInterval.Interval_60Min => (730, 730),
        _ => throw new FinanceNetException($"Unsupported interval {interval}"),
    };

    /// <summary>
    /// Maps the shared interval enum onto the notation the Yahoo chart endpoint expects ("15m" rather than "15min").
    /// </summary>
    private static string ToYahooInterval(EInterval interval) => interval switch
    {
        EInterval.Interval_1Min => "1m",
        EInterval.Interval_5Min => "5m",
        EInterval.Interval_15Min => "15m",
        EInterval.Interval_30Min => "30m",
        EInterval.Interval_60Min => "60m",
        _ => throw new FinanceNetException($"Unsupported interval {interval}"),
    };

    /// <summary>
    /// Projects the column-oriented chart payload into records, in exchange-local time.
    /// </summary>
    /// <remarks>
    /// Yahoo appends the current partial bar even when it lies outside the requested period,
    /// so the range is re-applied here rather than trusted from the response.
    /// </remarks>
    private static List<IntradayRecord> ParseIntradayRecords(ChartResult chartResult, DateTime startDate, DateTime endDate)
    {
        var records = new List<IntradayRecord>();
        var timestamps = chartResult.Timestamp;
        var quote = chartResult.Indicators?.Quote?.FirstOrDefault();
        if (timestamps == null || quote == null)
        {
            return records;
        }
        var offset = TimeSpan.FromSeconds(chartResult.Meta?.GmtOffset ?? 0);

        for (var i = 0; i < timestamps.Count; i++)
        {
            var open = ElementAtOrNull(quote.Open, i);
            var high = ElementAtOrNull(quote.High, i);
            var low = ElementAtOrNull(quote.Low, i);
            var close = ElementAtOrNull(quote.Close, i);
            if (open == null || high == null || low == null || close == null)
            {
                // Yahoo emits null columns for halted or untraded buckets
                continue;
            }
            var dateTime = (Helper.UnixToDateTime(timestamps[i]) ?? DateTime.UnixEpoch).Add(offset);
            if (dateTime.Date < startDate || dateTime.Date > endDate)
            {
                continue;
            }
            records.Add(new IntradayRecord
            {
                DateTime = dateTime,
                Open = open.Value,
                High = high.Value,
                Low = low.Value,
                Close = close.Value,
                Volume = ElementAtOrNull(quote.Volume, i) ?? 0,
            });
        }
        return records;
    }

    private static T? ElementAtOrNull<T>(List<T?>? values, int index) where T : struct
        => values != null && index < values.Count ? values[index] : null;

    /// <inheritdoc />
    public async Task<Quote> GetQuoteAsync(string symbol, CancellationToken token = default)
    {
        var quotes = await GetQuotesAsync([symbol], token).ConfigureAwait(false);
        return quotes.FirstOrDefault(e => e.Symbol == symbol);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Quote>> GetQuotesAsync(List<string> symbols, CancellationToken token = default)
    {
        await _yahooSession.RefreshSessionAsync(token).ConfigureAwait(false);
        var crumb = _yahooSession.GetApiCrumb();
        var httpClient = _httpClientFactory.CreateClient(Constants.YahooHttpClientName);
        var url = $"{Constants.YahooQuoteApiUrl}?" +
            $"&symbols={string.Join(",", symbols).ToLowerInvariant()}" +
            $"&crumb={crumb}";
        List<Quote> quotes;
        try
        {
            quotes = await _retryPolicy.ExecuteAsync(async ct =>
            {
                var fetched = new List<Quote>();

                var jsonContent = await Helper.FetchJsonDocumentAsync(httpClient, _logger, url, ct).ConfigureAwait(false);
                var parsedData = JsonConvert.DeserializeObject<QuoteResponseRoot>(jsonContent) ?? throw new FinanceNetException("Invalid data returned by Yahoo");
                var responseObj = parsedData.QuoteResponse ?? throw new FinanceNetException("Invalid content from Yahoo");

                var error = responseObj.Error;
                if (error != null)
                {
                    throw new FinanceNetException($"Received an error response from Yahoo: {error}");
                }
                if (responseObj.Result == null || responseObj.Result.Length == 0)
                {
                    throw new FinanceNetNoDataException($"Yahoo returned no data for {string.Join(", ", symbols)}");
                }

                foreach (var quoteResponse in responseObj.Result)
                {
                    if (quoteResponse.Symbol == null)
                    {
                        throw new FinanceNetException("Invalid quote field symbol");
                    }
                    var quote = quoteResponse.ToQuote();
                    fetched.Add(quote);
                }
                return fetched;
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not FinanceNetNoDataException)
        {
            throw new FinanceNetException("No way to fetch quotes", ex);
        }

        // Outside the try on purpose: a throwing logging sink must not turn a fetch that
        // already succeeded into a FinanceNetException.
        WarnAboutUnresolvedSymbols(symbols, quotes);
        return quotes;
    }

    private void WarnAboutUnresolvedSymbols(List<string> requested, List<Quote> quotes)
    {
        // Symbol is never null here - a null one is rejected while building the list above.
        var resolved = new HashSet<string>(quotes.Select(quote => quote.Symbol!), StringComparer.OrdinalIgnoreCase);
        var unresolved = requested
            .Where(symbol => !resolved.Contains(symbol))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unresolved.Count != 0)
        {
            _logger.LogWarning("Yahoo returned no data for {Count} of {RequestedCount} requested symbols: {Symbols}",
                unresolved.Count, requested.Count, string.Join(", ", unresolved));
        }
    }

    /// <inheritdoc />
    public Task<IEnumerable<Record>> GetRecordsAsync(string symbol, DateTime? startDate = null, DateTime? endDate = null, CancellationToken token = default)
        => GetRecordsAsync(symbol, startDate, endDate, EYahooInterval.Daily, token);

    /// <inheritdoc />
    public async Task<IEnumerable<Record>> GetRecordsAsync(string symbol, DateTime? startDate, DateTime? endDate, EYahooInterval interval, CancellationToken token = default)
    {
        await _yahooSession.RefreshSessionAsync(token).ConfigureAwait(false);
        var httpClient = _httpClientFactory.CreateClient(Constants.YahooHttpClientName);

        startDate ??= DateTime.UtcNow.AddDays(-7).Date;
        endDate ??= DateTime.UtcNow.Date;

        // Yahoo does not reliably return the period that contains period1, so ask from the start
        // of that period - Monday for weeks, the 1st for months. period2 is exclusive, so ask for
        // the day after the requested end date. Both are padded, see RequestPadding.
        var firstPeriodStart = GetPeriodStart(startDate.Value.Date, interval);
        var period1 = Helper.ToUnixTime(firstPeriodStart.Ticks > RequestPadding.Ticks ? firstPeriodStart - RequestPadding : DateTime.MinValue);
        var period2 = Helper.ToUnixTime(endDate.Value.Date.AddDays(1) + RequestPadding);

        // daily and coarser data is not subject to an intraday retention window, so the range
        // goes out unclamped and in one request - it reaches the first trading day
        var url = $"{Constants.YahooChartApiUrl}/{symbol}" +
            $"?interval={interval.GetDescription()}" +
            $"&period1={period1}" +
            $"&period2={period2}" +
            "&events=div%2Csplit";
        try
        {
            return await _retryPolicy.ExecuteAsync(async ct =>
            {
                var jsonContent = await FetchChartJsonAsync(httpClient, symbol, url, ct).ConfigureAwait(false);
                var parsedData = JsonConvert.DeserializeObject<ChartResponseRoot>(jsonContent) ?? throw new FinanceNetException("Invalid data returned by Yahoo");
                var chart = parsedData.Chart ?? throw new FinanceNetNoDataException($"Yahoo returned no records for {symbol}");

                if (chart.Error != null)
                {
                    throw new FinanceNetException($"Received an error response from Yahoo: {chart.Error}");
                }
                var chartResult = chart.Result?.FirstOrDefault() ?? throw new FinanceNetNoDataException($"Yahoo returned no records for {symbol}");

                var records = ParseRecords(chartResult, startDate.Value.Date, endDate.Value.Date, interval);
                return records.IsNullOrEmpty() ? throw new FinanceNetNoDataException($"Yahoo returned no records for {symbol}") : records;
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not FinanceNetNoDataException and not FinanceNetInvalidRequestException)
        {
            throw new FinanceNetException("No records found", ex);
        }
    }

    /// <summary>
    /// Projects the column-oriented chart payload into records, attaching any corporate action
    /// that fell on the same date.
    /// </summary>
    private static List<Record> ParseRecords(ChartResult chartResult, DateTime startDate, DateTime endDate, EYahooInterval interval)
    {
        var records = new List<Record>();
        var timestamps = chartResult.Timestamp;
        var quote = chartResult.Indicators?.Quote?.FirstOrDefault();
        if (timestamps == null || quote == null)
        {
            return records;
        }
        var offset = TimeSpan.FromSeconds(chartResult.Meta?.GmtOffset ?? 0);
        var adjClose = chartResult.Indicators?.AdjClose?.FirstOrDefault()?.AdjClose;
        var dividends = TotalEventsByPeriod(chartResult.Events?.Dividends, e => e.Date, e => ToDecimal(e.Amount), (a, b) => a + b, offset, interval);
        var splits = TotalEventsByPeriod(chartResult.Events?.Splits, e => e.Date, ToSplitCoefficient, (a, b) => a * b, offset, interval);

        for (var i = 0; i < timestamps.Count; i++)
        {
            var close = ElementAtOrNull(quote.Close, i);
            if (close == null)
            {
                // Yahoo emits null columns for halted or untraded periods
                continue;
            }
            var day = (Helper.UnixToDateTime(timestamps[i]) ?? DateTime.UnixEpoch).Add(offset).Date;
            var date = GetPeriodStart(day, interval);
            // a record is dated at the start of its period, so keep every period that overlaps
            // the range - including the one that began before startDate
            if (date > endDate || GetPeriodEnd(date, interval) <= startDate)
            {
                continue;
            }
            var record = new Record
            {
                Date = date,
                Open = ToDecimal(ElementAtOrNull(quote.Open, i)),
                High = ToDecimal(ElementAtOrNull(quote.High, i)),
                Low = ToDecimal(ElementAtOrNull(quote.Low, i)),
                Close = ToDecimal(close),
                AdjustedClose = ToDecimal(ElementAtOrNull(adjClose, i)) ?? ToDecimal(close),
                Volume = ElementAtOrNull(quote.Volume, i),
                Dividend = dividends.TryGetValue(date, out var dividend) ? dividend : null,
                SplitCoefficient = splits.TryGetValue(date, out var split) ? split : null,
            };

            // Yahoo sends an unfinished week or month as two bars: one up to yesterday, stamped
            // with the period's start, and one for today alone, stamped with the last trade.
            // Together they are the period so far. A daily bar already covers today, so a day is
            // never merged - Yahoo sends it once.
            if (interval != EYahooInterval.Daily && records.Count > 0 && records[^1].Date == date)
            {
                records[^1] = MergeWithToday(records[^1], record);
                continue;
            }
            records.Add(record);
        }

        // Yahoo serves oldest first, but the HTML page this replaced listed newest first,
        // as Alpha Vantage does - callers depend on that order.
        records.Reverse();
        return records;
    }

    /// <summary>
    /// Extends a period's record, which runs up to yesterday, with today's bar.
    /// </summary>
    private static Record MergeWithToday(Record period, Record today) => period with
    {
        High = period.High == null || today.High > period.High ? today.High : period.High,
        Low = period.Low == null || today.Low < period.Low ? today.Low : period.Low,
        Close = today.Close,
        AdjustedClose = today.AdjustedClose,
        Volume = period.Volume == null && today.Volume == null ? null : (period.Volume ?? 0) + (today.Volume ?? 0),
    };

    /// <summary>
    /// The date Yahoo stamps the record covering <paramref name="date"/> with.
    /// </summary>
    private static DateTime GetPeriodStart(DateTime date, EYahooInterval interval) => interval switch
    {
        EYahooInterval.Daily => date,
        // weeks run Monday to Sunday
        EYahooInterval.Weekly => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
        EYahooInterval.Monthly => new DateTime(date.Year, date.Month, 1, 0, 0, 0, date.Kind),
        _ => throw new FinanceNetException($"Unsupported interval {interval}"),
    };

    /// <summary>
    /// The first date after the period a record dated <paramref name="periodStart"/> covers.
    /// </summary>
    private static DateTime GetPeriodEnd(DateTime periodStart, EYahooInterval interval) => interval switch
    {
        EYahooInterval.Daily => periodStart.AddDays(1),
        EYahooInterval.Weekly => periodStart.AddDays(7),
        EYahooInterval.Monthly => periodStart.AddMonths(1),
        _ => throw new FinanceNetException($"Unsupported interval {interval}"),
    };

    /// <summary>
    /// Combines the corporate actions of each period, keyed by the date its record is stamped with.
    /// </summary>
    /// <remarks>
    /// Yahoo stamps a weekly or monthly record with the start of its period but reports each action
    /// on the day it happened, so an action is filed under the start of the period that day falls in.
    /// Several actions in one period are combined - dividends add up, split ratios multiply.
    /// </remarks>
    private static Dictionary<DateTime, decimal> TotalEventsByPeriod<TEvent>(
        Dictionary<string, TEvent>? events,
        Func<TEvent, long> getDate,
        Func<TEvent, decimal?> getValue,
        Func<decimal, decimal, decimal> combine,
        TimeSpan offset,
        EYahooInterval interval)
    {
        var totals = new Dictionary<DateTime, decimal>();
        foreach (var item in events?.Values ?? Enumerable.Empty<TEvent>())
        {
            var value = getValue(item);
            if (value == null)
            {
                continue;
            }
            var date = (Helper.UnixToDateTime(getDate(item)) ?? DateTime.UnixEpoch).Add(offset).Date;
            var period = GetPeriodStart(date, interval);
            totals[period] = totals.TryGetValue(period, out var total) ? combine(total, value.Value) : value.Value;
        }
        return totals;
    }

    private static decimal? ToSplitCoefficient(ChartSplit split)
        => split.Numerator is > 0 && split.Denominator is > 0
            ? ToDecimal(split.Numerator / split.Denominator)
            : null;

    private static decimal? ToDecimal(double? value) => value == null ? null : (decimal)value.Value;

    private async Task CheckAndDeclineConsentAsync(IHtmlDocument document, CancellationToken token)
    {
        var title = document.QuerySelector("title")?.TextContent ?? "";
        if (title.Contains("Lookup"))
        {
            _yahooSession.InvalidateSession();
            await _yahooSession.RefreshSessionAsync(token).ConfigureAwait(false);

            throw new FinanceNetException("Yahoo error: lookup received");
        }

        var consentDiv = document.QuerySelector("div#consent-page");
        if (consentDiv != null)
        {
            await _yahooSession.DeclineConsentAsync(document, token).ConfigureAwait(false);
            _logger.LogInformation("Consent declined");
        }
    }

    /// <inheritdoc />
    public async Task<Models.Yahoo.Profile> GetProfileAsync(string symbol, CancellationToken token = default)
    {
        await _yahooSession.RefreshSessionAsync(token).ConfigureAwait(false);
        var httpClient = _httpClientFactory.CreateClient(Constants.YahooHttpClientName);
        var url = $"{Constants.YahooQuoteHtmlUrl}/{symbol}/profile/".ToLowerInvariant();

        try
        {
            return await _retryPolicy.ExecuteAsync(async ct =>
            {
                var document = await Helper.FetchHtmlDocumentAsync(httpClient, _logger, url, ct).ConfigureAwait(false);
                await CheckAndDeclineConsentAsync(document, ct).ConfigureAwait(false);
                var result = YahooHtmlParser.ParseProfile(document);
                return result;
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not FinanceNetNoDataException)
        {
            throw new FinanceNetException("No profile found", ex);
        }
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, FinancialReport>> GetFinancialsAsync(string symbol, CancellationToken token = default)
    {
        await _yahooSession.RefreshSessionAsync(token).ConfigureAwait(false);
        var httpClient = _httpClientFactory.CreateClient(Constants.YahooHttpClientName);
        var url = $"{Constants.YahooQuoteHtmlUrl}/{symbol}/financials/".ToLowerInvariant();

        try
        {
            return await _retryPolicy.ExecuteAsync(async ct =>
            {
                var document = await Helper.FetchHtmlDocumentAsync(httpClient, _logger, url, ct).ConfigureAwait(false);
                await CheckAndDeclineConsentAsync(document, ct).ConfigureAwait(false);
                var result = YahooHtmlParser.ParseFinancialReports(document, _logger);
                return result;
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not FinanceNetNoDataException)
        {
            throw new FinanceNetException("No financial reports found", ex);
        }
    }

    /// <inheritdoc />
    public async Task<Summary> GetSummaryAsync(string symbol, CancellationToken token = default)
    {
        await _yahooSession.RefreshSessionAsync(token).ConfigureAwait(false);
        var httpClient = _httpClientFactory.CreateClient(Constants.YahooHttpClientName);
        var url = $"{Constants.YahooQuoteHtmlUrl}/{symbol}/?nojs=true".ToLowerInvariant();

        try
        {
            return await _retryPolicy.ExecuteAsync(async ct =>
            {
                var document = await Helper.FetchHtmlDocumentAsync(httpClient, _logger, url, ct).ConfigureAwait(false);
                await CheckAndDeclineConsentAsync(document, ct).ConfigureAwait(false);
                var result = YahooHtmlParser.ParseSummary(document, _logger);
                return result;
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not FinanceNetNoDataException)
        {
            throw new FinanceNetException("No summary found", ex);
        }
    }

    private async Task<IEnumerable<Instrument>> FetchSymbolsAsync(string baseUrl, EInstrumentType instrumentType, CancellationToken token = default)
    {
        var result = new List<Instrument>();
        await _yahooSession.RefreshSessionAsync(token).ConfigureAwait(false);
        var httpClient = _httpClientFactory.CreateClient(Constants.YahooHttpClientName);

        try
        {
            for (var i = 0; i < 100; i++)
            {
                var url = $"{baseUrl}?start={i * 100}&count=100".ToLowerInvariant();
                var items = await _retryPolicy.ExecuteAsync(async ct =>
                {
                    var document = await Helper.FetchHtmlDocumentAsync(httpClient, _logger, url, ct).ConfigureAwait(false);
                    await CheckAndDeclineConsentAsync(document, ct).ConfigureAwait(false);
                    var parsed = YahooHtmlParser.ParseSymbols(document, instrumentType, _logger);
                    return Helper.AreAllPropertiesNull(parsed)
                        ? throw new FinanceNetNoDataException($"Yahoo returned no {instrumentType} symbols")
                        : parsed;
                }, token).ConfigureAwait(false);

                result.AddRange(items.Where(e => e.Symbol != null));
                result = result
                    .GroupBy(stock => stock.Symbol)
                    .Select(group => group.First())
                    .ToList();

                if (items.Count < 100)
                {
                    break;
                }
            }

            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            if (result.IsNullOrEmpty())
            {
                throw;
            }
            else
            {
                return result;
            }
        }
    }

    /// <inheritdoc />
    public void InvalidateSession()
    {
        _yahooSession.InvalidateSession();
    }
}
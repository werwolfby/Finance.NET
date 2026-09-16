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
            var chunk = await GetIntradayRecordsChunkAsync(symbol, chunkStart, chunkEndExclusive, interval, token).ConfigureAwait(false);
            records.AddRange(chunk);
        }

        // newest first, matching GetRecordsAsync and the Alpha Vantage records
        records.Reverse();
        return records.IsNullOrEmpty()
            ? throw new FinanceNetNoDataException($"Yahoo returned no intraday records for {symbol}")
            : records;
    }

    private async Task<List<IntradayRecord>> GetIntradayRecordsChunkAsync(string symbol, DateTime startDate, DateTime endExclusive, EInterval interval, CancellationToken token)
    {
        var httpClient = _httpClientFactory.CreateClient(Constants.YahooHttpClientName);
        var url = $"{Constants.YahooChartApiUrl}/{symbol}" +
            $"?interval={ToYahooInterval(interval)}" +
            $"&period1={Helper.ToUnixTime(startDate)}" +
            $"&period2={Helper.ToUnixTime(endExclusive)}";
        try
        {
            return await _retryPolicy.ExecuteAsync(async ct =>
            {
                var jsonContent = await FetchChartJsonAsync(httpClient, url, ct).ConfigureAwait(false);
                var parsedData = JsonConvert.DeserializeObject<ChartResponseRoot>(jsonContent) ?? throw new FinanceNetException("Invalid data returned by Yahoo");
                var chart = parsedData.Chart ?? throw new FinanceNetNoDataException($"Yahoo returned no intraday records for {symbol}");

                if (chart.Error != null)
                {
                    throw new FinanceNetException($"Received an error response from Yahoo: {chart.Error}");
                }
                var chartResult = chart.Result?.FirstOrDefault() ?? throw new FinanceNetNoDataException($"Yahoo returned no intraday records for {symbol}");

                // filter against this chunk, so the trailing bar Yahoo appends cannot be added once per chunk
                return ParseIntradayRecords(chartResult, startDate, endExclusive.AddDays(-1));
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (FinanceNetNoDataException)
        {
            // a quiet chunk is not fatal - a neighbouring one may still carry records
            return [];
        }
        catch (Exception ex) when (ex is not FinanceNetInvalidRequestException)
        {
            throw new FinanceNetException($"No intraday records found for {symbol}", ex);
        }
    }

    /// <summary>
    /// Fetches the chart payload, turning the provider's rejection of a request into a
    /// <see cref="FinanceNetInvalidRequestException"/> so the retry policy leaves it alone.
    /// </summary>
    private async Task<string> FetchChartJsonAsync(HttpClient httpClient, string url, CancellationToken token)
    {
        var response = await httpClient.GetAsync(url, token).ConfigureAwait(false);
        var jsonContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var description = TryReadChartErrorDescription(jsonContent);
            throw new FinanceNetInvalidRequestException($"Yahoo rejected the request: {description ?? jsonContent}");
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
    /// How much intraday history Yahoo keeps per interval, and how much of it one request may span.
    /// </summary>
    /// <remarks>
    /// These are two different limits. 1m data is capped per request ("only 8 days worth of 1m
    /// granularity data are allowed to be fetched per request"), which chunking works around.
    /// The retention window is how far back the data exists at all, which chunking cannot extend.
    /// </remarks>
    private static (int MaxSpanDays, int RetentionDays) GetChartLimits(EInterval interval) => interval switch
    {
        // 8 days are allowed per request; stay a day inside that to keep boundaries safe
        EInterval.Interval_1Min => (7, 30),
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
    public async Task<IEnumerable<Record>> GetRecordsAsync(string symbol, DateTime? startDate = null, DateTime? endDate = null, EYahooInterval interval = EYahooInterval.Daily, CancellationToken token = default)
    {
        await _yahooSession.RefreshSessionAsync(token).ConfigureAwait(false);
        var httpClient = _httpClientFactory.CreateClient(Constants.YahooHttpClientName);

        startDate ??= DateTime.UtcNow.AddDays(-7).Date;
        endDate ??= DateTime.UtcNow.Date;

        // period2 is exclusive, so ask for the day after the requested end date
        var period1 = Helper.ToUnixTime(startDate.Value.Date);
        var period2 = Helper.ToUnixTime(endDate.Value.Date.AddDays(1));

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
                var jsonContent = await FetchChartJsonAsync(httpClient, url, ct).ConfigureAwait(false);
                var parsedData = JsonConvert.DeserializeObject<ChartResponseRoot>(jsonContent) ?? throw new FinanceNetException("Invalid data returned by Yahoo");
                var chart = parsedData.Chart ?? throw new FinanceNetNoDataException($"Yahoo returned no records for {symbol}");

                if (chart.Error != null)
                {
                    throw new FinanceNetException($"Received an error response from Yahoo: {chart.Error}");
                }
                var chartResult = chart.Result?.FirstOrDefault() ?? throw new FinanceNetNoDataException($"Yahoo returned no records for {symbol}");

                var records = ParseRecords(chartResult, startDate.Value.Date, endDate.Value.Date);
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
    private static List<Record> ParseRecords(ChartResult chartResult, DateTime startDate, DateTime endDate)
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
        var dividends = IndexEventsByDate(chartResult.Events?.Dividends, e => e.Date, offset);
        var splits = IndexEventsByDate(chartResult.Events?.Splits, e => e.Date, offset);

        for (var i = 0; i < timestamps.Count; i++)
        {
            var close = ElementAtOrNull(quote.Close, i);
            if (close == null)
            {
                // Yahoo emits null columns for halted or untraded periods
                continue;
            }
            var date = (Helper.UnixToDateTime(timestamps[i]) ?? DateTime.UnixEpoch).Add(offset).Date;
            if (date < startDate || date > endDate)
            {
                continue;
            }
            records.Add(new Record
            {
                Date = date,
                Open = ToDecimal(ElementAtOrNull(quote.Open, i)),
                High = ToDecimal(ElementAtOrNull(quote.High, i)),
                Low = ToDecimal(ElementAtOrNull(quote.Low, i)),
                Close = ToDecimal(close),
                AdjustedClose = ToDecimal(ElementAtOrNull(adjClose, i)) ?? ToDecimal(close),
                Volume = ElementAtOrNull(quote.Volume, i),
                Dividend = dividends.TryGetValue(date, out var dividend) ? ToDecimal(dividend.Amount) : null,
                SplitCoefficient = splits.TryGetValue(date, out var split) ? ToSplitCoefficient(split) : null,
            });
        }

        // Yahoo serves oldest first, but the HTML page this replaced listed newest first,
        // as Alpha Vantage does - callers depend on that order.
        records.Reverse();
        return records;
    }

    private static Dictionary<DateTime, TEvent> IndexEventsByDate<TEvent>(Dictionary<string, TEvent>? events, Func<TEvent, long> getDate, TimeSpan offset)
    {
        var indexed = new Dictionary<DateTime, TEvent>();
        if (events == null)
        {
            return indexed;
        }
        foreach (var item in events.Values)
        {
            var date = (Helper.UnixToDateTime(getDate(item)) ?? DateTime.UnixEpoch).Add(offset).Date;
            indexed[date] = item;
        }
        return indexed;
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
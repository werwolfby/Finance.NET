using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Finance.Net.Enums;
using Finance.Net.Exceptions;
using Finance.Net.Interfaces;
using Finance.Net.Models.Yahoo;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Finance.Net.Tests.IntegrationTests;

[TestFixture]
[Category("Integration")]
public class YahooFinanceTests
{
    private IYahooFinanceService _service;
    private ServiceProvider _serviceProvider;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _serviceProvider = TestHelper.SetUpServiceProvider();
        _service = _serviceProvider.GetRequiredService<IYahooFinanceService>();
    }

    [TearDown]
    public void TearDown()
    {
        Task.Delay(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult();
    }

    [TestCase(EInstrumentType.Index, 20)]
    [TestCase(EInstrumentType.Stock, 20)]
    [TestCase(EInstrumentType.ETF, 20)]
    [TestCase(EInstrumentType.Forex, 20)]
    [TestCase(EInstrumentType.Crypto, 20)]
    [TestCase(null, 100)]
    public async Task GetInstrumentsAsync(EInstrumentType? type, int expectedCnt)
    {
        var symbols = await _service.GetInstrumentsAsync(type);

        Assert.That(symbols, Is.Not.Empty);
        Assert.That(symbols.Count, Is.GreaterThanOrEqualTo(expectedCnt));
        Assert.That(symbols.All(e => !string.IsNullOrEmpty(e.Symbol)));
        Assert.That(symbols.Any(e => e.InstrumentType != null));

        Assert.Pass($"cnt = {symbols.Count()}");
    }

    [Test]
    public async Task GetRecordsAsync_WithDividend_Success()
    {
        var startDate = new DateTime(2020, 01, 01, 0, 0, 0, DateTimeKind.Utc);
        var records = await _service.GetRecordsAsync("SAP.DE", startDate);

        Assert.That(records, Is.Not.Empty);

        var lastRecentRecord = records.FirstOrDefault();
        Assert.That(lastRecentRecord.Date.Date <= DateTime.UtcNow, Is.True);
        Assert.That(lastRecentRecord.Date.Date >= startDate, Is.True);
    }

    [Test]
    public async Task GetRecordsAsync_FortyYears_ReachesFirstTradingDay()
    {
        // Daily data has no retention window, unlike intraday.
        var startDate = DateTime.UtcNow.Date.AddYears(-40);

        var records = (await _service.GetRecordsAsync("MSFT", startDate)).ToList();

        Assert.That(records, Has.Count.GreaterThan(9000));
        Assert.That(records.Select(e => e.Date), Is.Unique);
        Assert.That(records.All(e => e.Open != null && e.Close != null && e.AdjustedClose != null), Is.True);
        Assert.That(records.Min(e => e.Date).Year, Is.LessThanOrEqualTo(1990));

        Assert.Pass($"cnt = {records.Count}, first = {records.Min(e => e.Date):yyyy-MM-dd}");
    }

    [Test]
    public async Task GetRecordsAsync_OverASplitAndDividend_MapsCorporateActions()
    {
        // NVDA split 10:1 on 2024-06-10 and paid a dividend the day after.
        var startDate = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2024, 7, 30, 0, 0, 0, DateTimeKind.Utc);

        var records = (await _service.GetRecordsAsync("NVDA", startDate, endDate)).ToList();

        Assert.That(records.Any(e => e.SplitCoefficient == 10m), Is.True);
        Assert.That(records.Any(e => e.Dividend > 0), Is.True);
    }

    [TestCase("AIR.NZ", EYahooInterval.Weekly, "2024-02-01", "2024-05-31")]   // New Zealand left daylight saving on 2024-04-07
    [TestCase("AIR.NZ", EYahooInterval.Monthly, "2023-08-01", "2024-06-30")]  // ... and entered it on 2023-09-24
    [TestCase("MSFT", EYahooInterval.Weekly, "2024-09-01", "2024-12-31")]     // the US left it on 2024-11-03
    [TestCase("MSFT", EYahooInterval.Monthly, "2024-01-01", "2024-12-31")]
    public async Task GetRecordsAsync_CoarseInterval_ClosesWithItsLastDay(string symbol, EYahooInterval interval, string start, string end)
    {
        // A weekly or monthly record must hold the period it is dated with, so it closes with that
        // period's last day. Across a daylight-saving change, records used to land a period early.
        var startDate = DateTime.Parse(start, CultureInfo.InvariantCulture);
        var endDate = DateTime.Parse(end, CultureInfo.InvariantCulture);
        var periods = (await _service.GetRecordsAsync(symbol, startDate, endDate, interval)).ToList();
        var days = (await _service.GetRecordsAsync(symbol, startDate, endDate)).ToList();

        var checkedPeriods = 0;
        foreach (var period in periods)
        {
            var periodEnd = interval == EYahooInterval.Weekly ? period.Date.AddDays(7) : period.Date.AddMonths(1);
            if (period.Date < startDate || periodEnd > endDate.AddDays(1))
            {
                continue;   // only partly requested, so its last day may lie outside the range
            }
            var lastDay = days.Where(day => day.Date >= period.Date && day.Date < periodEnd).MaxBy(day => day.Date);
            Assert.That(period.Close, Is.EqualTo(lastDay.Close), $"{interval} record of {period.Date:yyyy-MM-dd}");
            checkedPeriods++;
        }
        Assert.That(checkedPeriods, Is.GreaterThan(3));
    }

    [Test]
    public async Task GetIntradayRecordsAsync_BeforeTheLastClockChange_KeepsNewYorkHours()
    {
        // Yahoo sends New York's offset now. A session from before the last clock change must still
        // run 09:30 to 15:30 - it used to come out an hour off. Several days are tried, as any one
        // may be a holiday or a short session.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var change = TestHelper.LastDaylightSavingChange(zone);
        var candidates = Enumerable.Range(1, 7)
            .Select(daysBefore => change.AddDays(-daysBefore))
            .Where(day => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            .Take(3);
        foreach (var day in candidates)
        {
            var bars = (await _service.GetIntradayRecordsAsync("MSFT", day, day, EInterval.Interval_60Min)).ToList();
            if (bars.Count >= 7)
            {
                Assert.That(bars.Min(e => e.DateTime.TimeOfDay), Is.EqualTo(new TimeSpan(9, 30, 0)));
                Assert.That(bars.Max(e => e.DateTime.TimeOfDay), Is.EqualTo(new TimeSpan(15, 30, 0)));
                return;
            }
        }
        Assert.Fail($"no full session in the week before {change:yyyy-MM-dd}");
    }

    [TestCase("SAP.DE")]
    [TestCase("8058.T")]
    [TestCase("MSFT")]
    public async Task GetRecordsAsync_LatestSession_IsIncludedWithItsClose(string symbol)
    {
        // Some exchanges get their daily close hours after the session ends. The session must not
        // go missing meanwhile - the newest day is the day of the newest intraday bar, at its price.
        var startDate = DateTime.UtcNow.Date.AddDays(-4);
        var bars = (await _service.GetIntradayRecordsAsync(symbol, startDate, null, EInterval.Interval_15Min)).ToList();
        var days = (await _service.GetRecordsAsync(symbol, startDate, DateTime.UtcNow.Date)).ToList();

        Assert.That(days[0].Date, Is.EqualTo(bars[0].DateTime.Date));
        Assert.That((double)days[0].Close.Value, Is.EqualTo(bars[0].Close).Within(1).Percent);
    }

    [TestCase("MSFT", EYahooInterval.Weekly)]
    [TestCase("8058.T", EYahooInterval.Weekly)]
    [TestCase("BTC-USD", EYahooInterval.Weekly)]
    [TestCase("MSFT", EYahooInterval.Monthly)]
    [TestCase("8058.T", EYahooInterval.Monthly)]
    [TestCase("BTC-USD", EYahooInterval.Monthly)]
    public async Task GetRecordsAsync_CoarseInterval_HasOneRecordPerPeriod(string symbol, EYahooInterval interval)
    {
        // Yahoo sends the unfinished period as two bars; they must come back as one record,
        // dated like every other - on a Monday, or on the 1st.
        var records = (await _service.GetRecordsAsync(symbol, DateTime.UtcNow.Date.AddDays(-100), DateTime.UtcNow.Date, interval)).ToList();

        Assert.That(records.Select(e => e.Date), Is.Unique);
        Assert.That(
            records.Select(e => e.Date),
            interval == EYahooInterval.Weekly
                ? Has.All.Matches<DateTime>(date => date.DayOfWeek == DayOfWeek.Monday)
                : Has.All.Matches<DateTime>(date => date.Day == 1));
    }

    [Test]
    public async Task GetRecordsAsync_CurrentWeek_MatchesItsDays()
    {
        // The current week is merged from Yahoo's two bars. Should Yahoo ever fold today into the
        // first while still sending the second, today would count twice - the week's own days
        // would no longer add up to it. Bitcoin trades every day, so the week always has days.
        var today = DateTime.UtcNow.Date;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));

        var days = (await _service.GetRecordsAsync("BTC-USD", monday, today)).ToList();
        var week = (await _service.GetRecordsAsync("BTC-USD", monday, today, EYahooInterval.Weekly)).Single();

        // a few seconds pass between the two requests while trading goes on
        Assert.That(week.Date, Is.EqualTo(monday));
        Assert.That(week.Volume, Is.EqualTo(days.Sum(e => e.Volume ?? 0)).Within(1).Percent);
        Assert.That(week.High, Is.EqualTo(days.Max(e => e.High)).Within(1).Percent);
        Assert.That(week.Low, Is.EqualTo(days.Min(e => e.Low)).Within(1).Percent);
        Assert.That(week.Close, Is.EqualTo(days[0].Close).Within(1).Percent);
    }

    [Test]
    public async Task GetRecordsAsync_Weekly_CarriesTheWeeksDividend()
    {
        // MSFT went ex-dividend on Wednesday 2024-05-15 - the week is stamped Monday 2024-05-13.
        var records = (await _service.GetRecordsAsync(
            "MSFT",
            new DateTime(2024, 5, 6, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 5, 26, 0, 0, 0, DateTimeKind.Utc),
            EYahooInterval.Weekly)).ToList();

        Assert.That(records.Single(e => e.Date == new DateTime(2024, 5, 13, 0, 0, 0, DateTimeKind.Utc)).Dividend, Is.EqualTo(0.75m));
        Assert.That(records.Count(e => e.Dividend != null), Is.EqualTo(1));
    }

    [Test]
    public async Task GetRecordsAsync_Monthly_CarriesTheMonthsSplit()
    {
        // TSLA split 3:1 on Thursday 2022-08-25 - the month is stamped 2022-08-01.
        var records = (await _service.GetRecordsAsync(
            "TSLA",
            new DateTime(2022, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2022, 9, 30, 0, 0, 0, DateTimeKind.Utc),
            EYahooInterval.Monthly)).ToList();

        Assert.That(records.Single(e => e.Date == new DateTime(2022, 8, 1, 0, 0, 0, DateTimeKind.Utc)).SplitCoefficient, Is.EqualTo(3m));
        Assert.That(records.Count(e => e.SplitCoefficient != null), Is.EqualTo(1));
    }

    [TestCase(EYahooInterval.Weekly, "2024-05-15", "2024-05-17", "2024-05-13")]
    [TestCase(EYahooInterval.Monthly, "2024-05-15", "2024-06-20", "2024-06-01,2024-05-01")]
    public async Task GetRecordsAsync_StartMidPeriod_ReturnsThePeriodContainingIt(EYahooInterval interval, string start, string end, string expected)
    {
        var records = await _service.GetRecordsAsync(
            "MSFT",
            DateTime.Parse(start, CultureInfo.InvariantCulture),
            DateTime.Parse(end, CultureInfo.InvariantCulture),
            interval);

        Assert.That(records.Select(e => e.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), Is.EqualTo(expected.Split(',')));
    }

    [TestCase(EYahooInterval.Daily, 200)]
    [TestCase(EYahooInterval.Weekly, 40)]
    [TestCase(EYahooInterval.Monthly, 10)]
    public async Task GetRecordsAsync_ByInterval_ReturnsCoarserSeries(EYahooInterval interval, int minimumCount)
    {
        var startDate = DateTime.UtcNow.Date.AddYears(-1);

        var records = (await _service.GetRecordsAsync("MSFT", startDate, null, interval)).ToList();

        Assert.That(records, Has.Count.GreaterThanOrEqualTo(minimumCount));
        Assert.That(records.Select(e => e.Date), Is.Unique);

        Assert.Pass($"{interval}: cnt = {records.Count}");
    }

    [TestCase("AAPL")]
    [TestCase("TSLA")]

    public async Task GetInstrumentsAsync_BySymbol_Success(string symbol)
    {
        var instruments = await _service.GetInstrumentsAsync(EInstrumentType.Stock);

        var instrument = instruments.FirstOrDefault(e => e.Symbol == symbol);
        Assert.That(instrument, Is.Not.Null);
        Assert.That(instrument.Symbol, Is.Not.Empty);

        Assert.Pass(JsonConvert.SerializeObject(instrument));
    }

    [TestCase("TSLA", true)]      // Tesla (Stock - Nasdaq)
    [TestCase("SAP.DE", true)]    // SAP SE (Stock - Xetra)
    [TestCase("8058.T", true)]    // Mitsubishi (Stock - Tokyo)
    [TestCase("VOO", true)]       // Vanguard S&P 500 (ETF)
    [TestCase("EURUSD=X", true)]  // Euro to USD (Forex)
    [TestCase("BTC-USD", true)]   // Bitcoin - USD (Crypto)
    [TestCase("^GSPC", true)]     // S&P 500 (Index)
    [TestCase("TESTING.NET", false)]
    public async Task GetRecordsAsync(string symbol, bool shouldHaveRecords)
    {
        var startDate = DateTime.UtcNow.AddDays(-7);
        if (shouldHaveRecords)
        {
            var records = await _service.GetRecordsAsync(symbol, startDate);

            Assert.That(records, Is.Not.Empty);
            Assert.That(records.All(e => e.Open != null), Is.True);

            var lastRecentRecord = records.FirstOrDefault();
            Assert.That(lastRecentRecord.Date.Date <= DateTime.UtcNow, Is.True);
            Assert.That(lastRecentRecord.Date.Date >= startDate, Is.True);

            Assert.Pass(JsonConvert.SerializeObject(lastRecentRecord));
        }
        else
        {
            // Yahoo's 404 for an unknown symbol is final - answered at once, not after the retry budget
            var elapsed = Stopwatch.StartNew();
            var exception = Assert.ThrowsAsync<FinanceNetNoDataException>(async () => await _service.GetRecordsAsync(symbol, startDate));
            Assert.That(exception.Message, Does.Contain(symbol));
            Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(30)));
        }
    }

    [TestCase("TSLA", EInterval.Interval_15Min)]   // Tesla (Stock - Nasdaq)
    [TestCase("SAP.DE", EInterval.Interval_60Min)] // SAP SE (Stock - Xetra)
    [TestCase("VOO", EInterval.Interval_5Min)]     // Vanguard S&P 500 (ETF)
    [TestCase("BTC-USD", EInterval.Interval_30Min)]// Bitcoin - USD (Crypto)
    public async Task GetIntradayRecordsAsync_Success(string symbol, EInterval interval)
    {
        var startDate = DateTime.UtcNow.AddDays(-5).Date;

        var records = (await _service.GetIntradayRecordsAsync(symbol, startDate, null, interval)).ToList();

        Assert.That(records, Is.Not.Empty);
        Assert.That(records.All(e => e.Open > 0 && e.High > 0 && e.Low > 0 && e.Close > 0), Is.True);
        Assert.That(records.All(e => e.High >= e.Low), Is.True);
        Assert.That(records.All(e => e.DateTime.Date >= startDate), Is.True);

        // sub-daily granularity: more than one record per calendar day
        Assert.That(records.Count, Is.GreaterThan(records.Select(e => e.DateTime.Date).Distinct().Count()));

        Assert.Pass($"cnt = {records.Count}, first = {records[0].DateTime:yyyy-MM-dd HH:mm}, last = {records[^1].DateTime:yyyy-MM-dd HH:mm}");
    }

    [TestCase("MSFT", "09:30", "16:00")]
    [TestCase("8058.T", "09:00", "15:30")]
    [TestCase("AIR.NZ", "10:00", "17:00")]
    [TestCase("BTC-USD", "00:00", "1.00:00")]
    public async Task GetIntradayRecordsAsync_RecentBars_AreWholeQuarterHoursWithinTheSession(string symbol, string sessionStart, string sessionEnd)
    {
        // Yahoo appends the latest price as a bar stamped with its own time - off the grid while
        // trading, at the session's end afterwards. It belongs to a bar on the grid, inside the session.
        var start = TimeSpan.Parse(sessionStart, CultureInfo.InvariantCulture);
        var end = TimeSpan.Parse(sessionEnd, CultureInfo.InvariantCulture);

        var records = (await _service.GetIntradayRecordsAsync(symbol, DateTime.UtcNow.Date.AddDays(-4), null, EInterval.Interval_15Min)).ToList();

        Assert.That(records.Select(e => e.DateTime), Has.All.Matches<DateTime>(time => time.Minute % 15 == 0 && time.Second == 0));
        Assert.That(records.Select(e => e.DateTime.TimeOfDay), Has.All.Matches<TimeSpan>(time => time >= start && time < end));
    }

    [Test]
    public async Task GetIntradayRecordsAsync_ExchangeAheadOfUtc_KeepsTheOpeningBars()
    {
        // NZX opens at 10:00 local, before UTC midnight, so a single-day request must still bring the morning.
        var hasMorning = await AnyRecentDayAsync(
            "AIR.NZ",
            day => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
            records => records.Min(e => e.DateTime).Hour < 12);

        Assert.That(hasMorning, Is.True);
    }

    [Test]
    public async Task GetIntradayRecordsAsync_ExchangeBehindUtc_KeepsTheEveningSession()
    {
        // E-mini futures trade on into the New York evening, past UTC midnight.
        var hasEvening = await AnyRecentDayAsync(
            "ES=F",
            day => day.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Thursday,
            records => records.Max(e => e.DateTime).Hour >= 20);

        Assert.That(hasEvening, Is.True);
    }

    /// <summary>
    /// Requests single recent days one at a time until one passes <paramref name="check"/>, so a
    /// market holiday on any one of them cannot fail the test.
    /// </summary>
    private async Task<bool> AnyRecentDayAsync(string symbol, Func<DateTime, bool> isCandidate, Func<List<IntradayRecord>, bool> check)
    {
        var candidates = Enumerable.Range(1, 10)
            .Select(daysBack => DateTime.UtcNow.Date.AddDays(-daysBack))
            .Where(isCandidate)
            .Take(3);
        foreach (var day in candidates)
        {
            try
            {
                var records = (await _service.GetIntradayRecordsAsync(symbol, day, day, EInterval.Interval_5Min)).ToList();
                if (check(records))
                {
                    return true;
                }
            }
            catch (FinanceNetNoDataException)
            {
                // a holiday - try the next day
            }
        }
        return false;
    }

    [Test]
    public void GetIntradayRecordsAsync_InvalidSymbol_Throws()
    {
        var startDate = DateTime.UtcNow.AddDays(-5).Date;

        // Yahoo's 404 for an unknown symbol is final - answered at once, not after the retry budget
        var elapsed = Stopwatch.StartNew();
        var exception = Assert.ThrowsAsync<FinanceNetNoDataException>(async () => await _service.GetIntradayRecordsAsync("TESTING.NET", startDate));
        Assert.That(exception.Message, Does.Contain("TESTING.NET"));
        Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public async Task GetIntradayRecordsAsync_TenYearsOfHourlyData_ReturnsWhatYahooKeeps()
    {
        // Yahoo keeps 730 days of hourly data, so a 10 year request is truncated rather than rejected.
        var startDate = DateTime.UtcNow.AddYears(-10).Date;

        var records = (await _service.GetIntradayRecordsAsync("MSFT", startDate, null, EInterval.Interval_60Min)).ToList();

        Assert.That(records, Is.Not.Empty);
        Assert.That(records.All(e => e.Open > 0 && e.High >= e.Low), Is.True);
        Assert.That(records.Min(e => e.DateTime).Date, Is.GreaterThanOrEqualTo(DateTime.UtcNow.Date.AddDays(-731)));

        Assert.Pass($"cnt = {records.Count}, first = {records.Min(e => e.DateTime):yyyy-MM-dd}, last = {records.Max(e => e.DateTime):yyyy-MM-dd}");
    }

    [Test]
    public async Task GetIntradayRecordsAsync_SpanningMultipleChunks_ReturnsContiguousMinuteBars()
    {
        // 1m data is capped at 8 days per request, so 20 days exercises the chunking.
        var startDate = DateTime.UtcNow.AddDays(-20).Date;

        var records = (await _service.GetIntradayRecordsAsync("MSFT", startDate, null, EInterval.Interval_1Min)).ToList();

        Assert.That(records, Is.Not.Empty);
        Assert.That(records.Select(e => e.DateTime), Is.Unique);
        Assert.That(records.Select(e => e.DateTime.Date).Distinct().Count(), Is.GreaterThan(8));

        Assert.Pass($"cnt = {records.Count}, days = {records.Select(e => e.DateTime.Date).Distinct().Count()}");
    }

    [Test]
    public void GetIntradayRecordsAsync_RangeBeyondRetention_ThrowsInvalidRequest()
    {
        var startDate = new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2017, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.ThrowsAsync<FinanceNetInvalidRequestException>(async () =>
            await _service.GetIntradayRecordsAsync("MSFT", startDate, endDate, EInterval.Interval_60Min));
    }

    [TestCase("TSLA", true)]      // Tesla (Stock - Nasdaq)
    [TestCase("SAP.DE", true)]    // SAP SE (Stock - Xetra)
    [TestCase("8058.T", true)]    // Mitsubishi (Stock - Tokyo)
    [TestCase("VOO", true)]       // Vanguard S&P 500 (ETF)
    [TestCase("EURUSD=X", true)]  // Euro to USD (Forex)
    [TestCase("BTC-USD", true)]   // Bitcoin - USD (Crypto)
    [TestCase("^GSPC", true)]     // S&P 500 (Index)
    [TestCase("TESTING.NET", false)]
    public async Task GetQuoteAsync(string symbol, bool shouldHaveQuote)
    {
        if (shouldHaveQuote)
        {
            var quote = await _service.GetQuoteAsync(symbol);

            Assert.That(quote, Is.Not.Null);
            Assert.That(quote.Symbol, Is.EqualTo(symbol));
            Assert.That(quote.FirstTradeDate.Value.Date >= new DateTime(1920, 1, 1, 0, 0, 0, DateTimeKind.Utc) &&
                        quote.FirstTradeDate.Value.Date <= DateTime.UtcNow, Is.True);
            Assert.That(!string.IsNullOrWhiteSpace(quote.QuoteType), Is.True);
            Assert.That(!string.IsNullOrWhiteSpace(quote.Exchange), Is.True);
            Assert.That(!string.IsNullOrWhiteSpace(quote.ShortName), Is.True);
            Assert.That(!string.IsNullOrWhiteSpace(quote.LongName), Is.True);

            Assert.Pass(JsonConvert.SerializeObject(quote));
        }
        else
        {
            Assert.ThrowsAsync<FinanceNetException>(async () => await _service.GetQuoteAsync(symbol));
        }
    }

    [TestCase("TSLA", true)]      // Tesla (Stock - Nasdaq)
    [TestCase("SAP.DE", true)]    // SAP SE (Stock - Xetra)
    [TestCase("8058.T", true)]    // Mitsubishi (Stock - Tokyo)
    [TestCase("VOO", true)]       // Vanguard S&P 500 (ETF)
    [TestCase("EURUSD=X", false)]  // Euro to USD (Forex)
    [TestCase("BTC-USD", true)]   // Bitcoin - USD (Crypto)
    [TestCase("^GSPC", false)]     // S&P 500 (Index)
    [TestCase("TESTING.NET", false)]
    public async Task GetProfileAsync(string symbol, bool shouldHaveData)
    {
        if (shouldHaveData)
        {
            var profile = await _service.GetProfileAsync(symbol);

            Assert.That(profile, Is.Not.Null);
            Assert.That(profile.Description, Is.Not.Empty);

            if (symbol != "BTC-USD")
            {
                Assert.That(profile.Industry, Is.Not.Empty);
                Assert.That(profile.Sector, Is.Not.Empty);
                Assert.That(profile.Phone, Is.Not.Empty);
                Assert.That(profile.Adress, Is.Not.Empty);
                Assert.That(profile.Website, Is.Not.Empty);
            }

            Assert.Pass(JsonConvert.SerializeObject(profile));
        }
        else
        {
            Assert.ThrowsAsync<FinanceNetException>(async () => await _service.GetProfileAsync(symbol));
        }
    }


    [TestCase("TSLA", true)]      // Tesla (Stock - Nasdaq)
    [TestCase("SAP.DE", true)]    // SAP SE (Stock - Xetra)
    [TestCase("8058.T", true)]    // Mitsubishi (Stock - Tokyo)
    [TestCase("VOO", true)]       // Vanguard S&P 500 (ETF)
    [TestCase("EURUSD=X", true)]  // Euro to USD (Forex)
    [TestCase("BTC-USD", true)]   // Bitcoin - USD (Crypto)
    [TestCase("^GSPC", true)]     // S&P 500 (Index)
    [TestCase("TESTING.NET", false)]
    public async Task GetSummaryAsync(string symbol, bool shouldHaveData)
    {
        if (shouldHaveData)
        {
            var summary = await _service.GetSummaryAsync(symbol);

            Assert.That(summary, Is.Not.Null);

            Assert.That(summary.Name, Is.Not.Empty);
            Assert.That(summary.PreviousClose, Is.Not.Null);

            Assert.Pass(JsonConvert.SerializeObject(summary));
        }
        else
        {
            Assert.ThrowsAsync<FinanceNetException>(async () => await _service.GetSummaryAsync(symbol));
        }
    }

    [TestCase("TSLA", true)]      // Tesla (Stock - Nasdaq)
    [TestCase("SAP.DE", true)]    // SAP SE (Stock - Xetra)
    [TestCase("8058.T", true)]    // Mitsubishi (Stock - Tokyo)
    [TestCase("VOO", false)]       // Vanguard S&P 500 (ETF)
    [TestCase("EURUSD=X", false)]  // Euro to USD (Forex)
    [TestCase("BTC-USD", false)]   // Bitcoin - USD (Crypto)
    [TestCase("^GSPC", false)]     // S&P 500 (Index)
    [TestCase("TESTING.NET", false)]
    public async Task GetFinancialsAsync(string symbol, bool shouldHaveData)
    {
        if (shouldHaveData)
        {
            var financials = await _service.GetFinancialsAsync(symbol);

            Assert.That(financials, Is.Not.Empty);

            var firstReport = financials.FirstOrDefault();
            Assert.That(!string.IsNullOrWhiteSpace(firstReport.Key));
            Assert.That(firstReport.Value.TotalRevenue > 0);

            Assert.Pass(JsonConvert.SerializeObject(financials));
        }
        else
        {
            Assert.ThrowsAsync<FinanceNetException>(async () => await _service.GetFinancialsAsync(symbol));
        }
    }
}

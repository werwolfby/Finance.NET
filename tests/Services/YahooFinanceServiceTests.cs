using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Finance.Net.Enums;
using Finance.Net.Exceptions;
using Finance.Net.Interfaces;
using Finance.Net.Models.Yahoo;
using Finance.Net.Services;
using Finance.Net.Utilities;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using NUnit.Framework;
using Polly;
using Polly.Registry;

namespace Finance.Net.Tests.Services;

[TestFixture]
[Category("Unit")]
public class YahooFinanceServiceTests
{
    private Mock<ILogger<YahooFinanceService>> _mockLogger;
    private Mock<IHttpClientFactory> _mockHttpClientFactory;
    private Mock<IYahooSessionManager> _mockYahooSession;
    private Mock<HttpMessageHandler> _mockHandler;
    private Mock<IReadOnlyPolicyRegistry<string>> _mockPolicyRegistry;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _mockLogger = new Mock<ILogger<YahooFinanceService>>();
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockHandler = new Mock<HttpMessageHandler>();
        _mockYahooSession = new Mock<IYahooSessionManager>();
        _mockPolicyRegistry = new Mock<IReadOnlyPolicyRegistry<string>>();

        var realPolicy = Policy.Handle<Exception>().RetryAsync(1);
        _mockPolicyRegistry
            .Setup(registry => registry.Get<IAsyncPolicy>(Constants.DefaultHttpRetryPolicy))
            .Returns(realPolicy);

        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("", Encoding.UTF8, "text/html"),
                Headers =
                {
                    { "Set-Cookie", "\"A3=d=AQABBIPiUmcCEKLS0S2dxFEvSY2wq0BTJc4FEgEBAQE0VGdcZ-AMyiMA_eMAAA&S=AQAAAueeOka9YBgG-7Z2662G2t0; Expires=Mo, 10 Dec 2040 17:39:47 GMT; Max-Age=99931557600; Domain=.yahoo.com; Path=/; SameSite=None; Secure; HttpOnly\"" } // Add custom headers here
                            }
            });
        _mockHttpClientFactory.Setup(e => e.CreateClient(It.IsAny<string>())).Returns(new HttpClient(_mockHandler.Object));
    }

    [Test]
    public void Constructor_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new YahooFinanceService(
            null,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object));
        Assert.Throws<ArgumentNullException>(() => new YahooFinanceService(
            _mockLogger.Object,
            null,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object));
        Assert.Throws<ArgumentNullException>(() => new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            null,
            _mockYahooSession.Object));
        Assert.Throws<ArgumentNullException>(() => new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            null));
    }

    [Test]
    public async Task GetQuoteAsync_WithResponse_ReturnsResult()
    {
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "quote.json");
        SetupHttpJsonFileResponse(filePath);

        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var result = await service.GetQuoteAsync("IBM");

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Symbol, Is.EqualTo("IBM"));
        Assert.That(!string.IsNullOrWhiteSpace(result.ShortName));
        Assert.That(result.MarketCap > 0);
    }

    [Test]
    public void GetQuoteAsync_NoResponse_Throws()
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("", Encoding.UTF8, "text/html"),
            });
        _mockHttpClientFactory.Setup(e => e.CreateClient(It.IsAny<string>())).Returns(new HttpClient(_mockHandler.Object));

        // Arrange
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var exception = Assert.ThrowsAsync<FinanceNetException>(async () => await service.GetQuoteAsync("IBM"));
        Assert.That(exception.InnerException.Message, Does.Contain("Invalid data"));
    }

    [Test]
    public void GetQuoteAsync_EmptyResponse_Throws()
    {
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "empty.json");
        SetupHttpHtmlFileResponse(filePath);
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var exception = Assert.ThrowsAsync<FinanceNetException>(async () => await service.GetQuoteAsync("IBM"));
        Assert.That(exception.InnerException.Message, Does.Contain("Invalid content"));
    }

    [Test]
    public void InvalidateSession_ShouldCallYahooSessionInvalidateSession()
    {
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        service.InvalidateSession();

        // Assert
        _mockYahooSession.Verify(session => session.InvalidateSession(), Times.Once);
    }

    [Test]
    public void GetQuoteAsync_ErrorResponse_Throws()
    {
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "quote_error_response.json");
        SetupHttpJsonFileResponse(filePath);

        // Arrange
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        Assert.ThrowsAsync<FinanceNetException>(async () => await service.GetQuoteAsync("IBM"));
    }

    [Test]
    public void GetQuoteAsync_InvalidResponse_Throws()
    {
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "quote_invalid_response.json");
        SetupHttpJsonFileResponse(filePath);

        // Arrange
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        Assert.ThrowsAsync<FinanceNetException>(async () => await service.GetQuoteAsync("SAP"));
    }

    [Test]
    public async Task GetQuotesAsync_WithResponse_ReturnsResult()
    {
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "quote.json");
        SetupHttpJsonFileResponse(filePath);

        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        var symbols = new List<string> { "IBM" };

        // Act
        var result = await service.GetQuotesAsync(symbols);

        // Assert
        Assert.That(result, Is.Not.Empty);
        var first = result.FirstOrDefault();
        Assert.That(first.Symbol, Is.EqualTo(symbols.FirstOrDefault()));
        Assert.That(!string.IsNullOrWhiteSpace(first.ShortName));
        Assert.That(first.MarketCap > 0);
    }

    [Test]
    public async Task GetProfileAsync_WithResponse_ReturnsResult()
    {
        // Arrange
        var filePathProfile = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "profile.html");
        SetupHttpHtmlFileResponses([filePathProfile]);

        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var result = await service.GetProfileAsync("IBM");

        // Assert
        Assert.That(result.Adress, Is.Not.Empty);
        Assert.That(result.Sector, Is.Not.Empty);
        Assert.That(result.Industry, Is.Not.Empty);
        Assert.That(result.Description, Is.Not.Empty);
    }

    [Test]
    public void GetProfileAsync_NoResponse_Throws()
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("", Encoding.UTF8, "text/html"),
            });
        _mockHttpClientFactory.Setup(e => e.CreateClient(It.IsAny<string>())).Returns(new HttpClient(_mockHandler.Object));

        // Arrange
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var exception = Assert.ThrowsAsync<FinanceNetException>(async () => await service.GetProfileAsync("IBM"));
        Assert.That(exception.InnerException.Message, Does.Contain("received 200, but no html content"));
    }

    [Test]
    public void GetProfileAsync_EmptyResponse_Throws()
    {
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "empty.html");
        SetupHttpHtmlFileResponse(filePath);
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var exception = Assert.ThrowsAsync<FinanceNetException>(async () => await service.GetProfileAsync("IBM"));
        Assert.That(exception, Is.Not.InstanceOf<FinanceNetNoDataException>(), "an unexpected page is not a permanent no-data answer");
        Assert.That(exception.Message, Does.Contain("No profile found"));
    }

    [Test]
    public async Task GetSummaryAsync_WithResponse_ReturnsResult()
    {
        // Arrange
        var filePathSummary = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "summary.html");
        SetupHttpHtmlFileResponses([filePathSummary]);

        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var result = await service.GetSummaryAsync("IBM");

        // Assert
        Assert.That(result.Name, Is.Not.Empty);
        Assert.That(result.PreviousClose, Is.Not.Null);
        Assert.That(result.Ask, Is.Not.Null);
        Assert.That(result.Bid, Is.Not.Null);
        Assert.That(result.MarketCap_Intraday, Is.Not.Null);
    }

    [Test]
    public void GetSummaryAsync_NoResponse_Throws()
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("", Encoding.UTF8, "text/html"),
            });
        _mockHttpClientFactory.Setup(e => e.CreateClient(It.IsAny<string>())).Returns(new HttpClient(_mockHandler.Object));

        // Arrange
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var exception = Assert.ThrowsAsync<FinanceNetException>(async () => await service.GetSummaryAsync("IBM"));
        Assert.That(exception.Message, Does.Contain("No summary"));
        Assert.That(exception.InnerException.Message, Does.Contain("received 200, but no html content"));
    }

    [Test]
    public void GetSummaryAsync_EmptyResponse_Throws()
    {
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "empty.html");
        SetupHttpHtmlFileResponse(filePath);
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var exception = Assert.ThrowsAsync<FinanceNetException>(async () => await service.GetSummaryAsync("IBM"));
        Assert.That(exception, Is.Not.InstanceOf<FinanceNetNoDataException>(), "an unexpected page is not a permanent no-data answer");
        Assert.That(exception.Message, Does.Contain("No summary found"));
    }

    [Test]
    public async Task GetFinancialsAsync_WithResponse_ReturnsResult()
    {
        // Arrange
        var filePathFinancial = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "financial_eu.html");
        SetupHttpHtmlFileResponses([filePathFinancial]);

        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var result = await service.GetFinancialsAsync("IBM");

        // Assert
        Assert.That(result, Is.Not.Empty);

        var first = result.First();
        Assert.That(!string.IsNullOrWhiteSpace(first.Key));
        Assert.That(first.Value.TotalRevenue != null);
        Assert.That(first.Value.BasicAverageShares != null);
        Assert.That(first.Value.EBIT != null);
    }

    [Test]
    public void GetFinancialsAsync_EmptyResponse_Throws()
    {
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "empty.html");
        SetupHttpHtmlFileResponse(filePath);

        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var exception = Assert.ThrowsAsync<FinanceNetException>(async () => await service.GetFinancialsAsync("IBM"));

        Assert.That(exception.InnerException.Message, Does.Contain("no content"));
    }

    [Test]
    public void GetFinancialsAsync_NoResponse_Throws()
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("", Encoding.UTF8, "text/html"),
            });
        _mockHttpClientFactory.Setup(e => e.CreateClient(It.IsAny<string>())).Returns(new HttpClient(_mockHandler.Object));

        // Arrange
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var exception = Assert.ThrowsAsync<FinanceNetException>(async () => await service.GetFinancialsAsync("IBM"));
        Assert.That(exception.InnerException.Message, Does.Contain("received 200, but no html content"));
    }

    [Test]
    public async Task GetRecordsAsync_WithResponse_ReturnsResult()
    {
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "records.json");
        SetupHttpJsonFileResponse(filePath);
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var result = (await service.GetRecordsAsync("NVDA", new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 7, 30, 0, 0, 0, DateTimeKind.Utc))).ToList();

        // Assert
        Assert.That(result, Is.Not.Empty);
        Assert.That(result.All(e => e.Open != null));
        Assert.That(result.All(e => e.Close != null));
        Assert.That(result.All(e => e.AdjustedClose != null));
        Assert.That(result.All(e => e.Low != null));
        Assert.That(result.All(e => e.High != null));
        Assert.That(result.Select(e => e.Date), Is.Unique);
    }

    [Test]
    public async Task GetRecordsAsync_Always_ReturnsNewestFirst()
    {
        // The HTML history page listed newest first and Alpha Vantage does the same,
        // so callers depend on this ordering.
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "records.json");
        SetupHttpJsonFileResponse(filePath);
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var result = (await service.GetRecordsAsync("NVDA", new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 7, 30, 0, 0, 0, DateTimeKind.Utc))).ToList();

        // Assert
        Assert.That(result, Is.Ordered.Descending.By(nameof(Record.Date)));
    }

    [Test]
    public async Task GetIntradayRecordsAsync_Always_ReturnsNewestFirst()
    {
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "intraday_records.json");
        SetupHttpJsonFileResponse(filePath);
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var result = (await service.GetIntradayRecordsAsync(
            "IBM",
            new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc),
            EInterval.Interval_15Min)).ToList();

        // Assert
        Assert.That(result, Is.Ordered.Descending.By(nameof(IntradayRecord.DateTime)));
    }

    [Test]
    public async Task GetRecordsAsync_WithCorporateActions_MapsDividendsAndSplits()
    {
        // The fixture is NVDA around its 10:1 split on 2024-06-10 and its dividend the day after.
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "records.json");
        SetupHttpJsonFileResponse(filePath);
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var result = (await service.GetRecordsAsync("NVDA", new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 7, 30, 0, 0, 0, DateTimeKind.Utc))).ToList();

        // Assert
        var splitDay = result.Single(e => e.Date.Date == new DateTime(2024, 6, 10, 0, 0, 0, DateTimeKind.Utc));
        Assert.That(splitDay.SplitCoefficient, Is.EqualTo(10m));

        var dividendDay = result.Single(e => e.Date.Date == new DateTime(2024, 6, 11, 0, 0, 0, DateTimeKind.Utc));
        Assert.That(dividendDay.Dividend, Is.EqualTo(0.01m));

        // days without a corporate action carry none
        Assert.That(result.Count(e => e.SplitCoefficient != null), Is.EqualTo(1));
        Assert.That(result.Count(e => e.Dividend != null), Is.EqualTo(1));
    }

    [Test]
    public async Task GetRecordsAsync_WithoutInterval_StillRequestsDaily()
    {
        // The four-argument overload is the shape callers compiled against before intervals
        // existed. It has to keep meaning "daily", positional token and all.
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "records.json");
        var requests = SetupHttpResponseCapturingRequests(HttpStatusCode.OK, await File.ReadAllTextAsync(filePath));
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        await service.GetRecordsAsync(
            "NVDA",
            new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 7, 30, 0, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        Assert.That(requests, Has.Count.EqualTo(1));
        Assert.That(requests[0], Does.Contain("interval=1d"));
    }

    [TestCase(EYahooInterval.Weekly, "records_weekly_current_period.json", "2026-09-14", 497.06, 509.95, 491.13, 493.6, 47767210L, "2026-09-07")]
    [TestCase(EYahooInterval.Monthly, "records_monthly_current_period.json", "2026-09-01", 497.52, 515.65, 486.0, 493.65, 188699448L, "2026-08-01")]
    public async Task GetRecordsAsync_CurrentPeriod_MergesYahoosTwoBarsIntoOneRecord(
        EYahooInterval interval, string fixture, string current, double open, double high, double low, double close, long volume, string previous)
    {
        // Yahoo sends the unfinished period as two bars: one up to yesterday, stamped with the
        // period's start, and one for today alone, stamped with the time of the last trade.
        // Together they are the period so far - one record, not two.
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", fixture);
        SetupHttpResponseCapturingRequests(HttpStatusCode.OK, await File.ReadAllTextAsync(filePath));
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var result = (await service.GetRecordsAsync(
            "MSFT",
            new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
            interval)).ToList();

        // Assert - the period's open, the widest range, today's close, both volumes
        Assert.That(result.Select(e => e.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), Is.EqualTo(new[] { current, previous }));
        var merged = result[0];
        Assert.That(merged.Open, Is.EqualTo((decimal)open));
        Assert.That(merged.High, Is.EqualTo((decimal)high));
        Assert.That(merged.Low, Is.EqualTo((decimal)low));
        Assert.That(merged.Close, Is.EqualTo((decimal)close));
        Assert.That(merged.AdjustedClose, Is.EqualTo((decimal)close));
        Assert.That(merged.Volume, Is.EqualTo(volume));
    }

    [Test]
    public async Task GetRecordsAsync_Weekly_AttachesCorporateActionsToTheirWeek()
    {
        // A weekly record is stamped with its Monday, but each action carries the day it happened.
        // Every action in the week belongs to that record - dividends add up, splits compound.
        // Arrange
        var weeks = new[] { NewYorkToUnix(2024, 5, 6), NewYorkToUnix(2024, 5, 13), NewYorkToUnix(2024, 5, 20) };
        var events = BuildEventsJson(
            [(NewYorkToUnix(2024, 5, 13, 9, 30), 0.75), (NewYorkToUnix(2024, 5, 16, 9, 30), 1.0)],
            [(NewYorkToUnix(2024, 5, 15, 9, 30), 2, 1), (NewYorkToUnix(2024, 5, 24, 9, 30), 3, 1)]);
        SetupHttpResponseCapturingRequests(HttpStatusCode.OK, BuildChartJson(weeks, NewYorkSummerOffset, events));
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var result = (await service.GetRecordsAsync(
            "MSFT",
            new DateTime(2024, 5, 6, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 5, 26, 0, 0, 0, DateTimeKind.Utc),
            EYahooInterval.Weekly)).ToList();

        // Assert - newest first
        Assert.That(result.Select(e => e.Dividend), Is.EqualTo(new decimal?[] { null, 1.75m, null }));
        Assert.That(result.Select(e => e.SplitCoefficient), Is.EqualTo(new decimal?[] { 3m, 2m, null }));
    }

    [Test]
    public async Task GetRecordsAsync_Monthly_AttachesCorporateActionsToTheirMonth()
    {
        // Arrange
        var months = new[] { NewYorkToUnix(2024, 4, 1), NewYorkToUnix(2024, 5, 1), NewYorkToUnix(2024, 6, 1) };
        var events = BuildEventsJson(
            [(NewYorkToUnix(2024, 5, 15, 9, 30), 0.75)],
            [(NewYorkToUnix(2024, 6, 3, 9, 30), 2, 1), (NewYorkToUnix(2024, 6, 20, 9, 30), 3, 1)]);
        SetupHttpResponseCapturingRequests(HttpStatusCode.OK, BuildChartJson(months, NewYorkSummerOffset, events));
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var result = (await service.GetRecordsAsync(
            "MSFT",
            new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            EYahooInterval.Monthly)).ToList();

        // Assert - newest first
        Assert.That(result.Select(e => e.Dividend), Is.EqualTo(new decimal?[] { null, 0.75m, null }));
        Assert.That(result.Select(e => e.SplitCoefficient), Is.EqualTo(new decimal?[] { 6m, null, null }));
    }

    [TestCase(EYahooInterval.Daily, "1d")]
    [TestCase(EYahooInterval.Weekly, "1wk")]
    [TestCase(EYahooInterval.Monthly, "1mo")]
    public async Task GetRecordsAsync_ByInterval_RequestsMatchingGranularity(EYahooInterval interval, string expected)
    {
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "records.json");
        var requests = SetupHttpResponseCapturingRequests(HttpStatusCode.OK, await File.ReadAllTextAsync(filePath));
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        await service.GetRecordsAsync("NVDA", new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 7, 30, 0, 0, 0, DateTimeKind.Utc), interval);

        // Assert
        Assert.That(requests, Has.Count.EqualTo(1));
        Assert.That(requests[0], Does.Contain($"interval={expected}"));
    }

    [TestCase(EYahooInterval.Weekly, "2024-05-15", "2024-05-17", new[] { "2024-05-13" }, false)]
    [TestCase(EYahooInterval.Weekly, "2024-05-15", "2024-05-17", new[] { "2024-05-13" }, true)]
    [TestCase(EYahooInterval.Monthly, "2024-05-15", "2024-06-20", new[] { "2024-06-01", "2024-05-01" }, false)]
    [TestCase(EYahooInterval.Monthly, "2024-05-15", "2024-06-20", new[] { "2024-06-01", "2024-05-01" }, true)]
    public async Task GetRecordsAsync_StartMidPeriod_ReturnsThePeriodContainingIt(EYahooInterval interval, string start, string end, string[] expected, bool yahooServesWholePeriods)
    {
        // Yahoo dates a weekly or monthly record at the start of its period (Monday, the 1st), so
        // the record covering startDate is dated before it - it must still be returned.
        // Arrange
        var periodStarts = interval == EYahooInterval.Weekly
            ? Enumerable.Range(0, 53).Select(week => new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(7 * week)).ToList()
            : Enumerable.Range(1, 12).Select(month => new DateTime(2024, month, 1, 0, 0, 0, DateTimeKind.Utc)).ToList();
        var periods = periodStarts.Select(periodStart => (periodStart, interval == EYahooInterval.Weekly ? periodStart.AddDays(7) : periodStart.AddMonths(1)));
        SetupChartEndpoint(-4 * 3600, periods, yahooServesWholePeriods);
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var result = await service.GetRecordsAsync(
            "MSFT",
            DateTime.Parse(start, CultureInfo.InvariantCulture),
            DateTime.Parse(end, CultureInfo.InvariantCulture),
            interval);

        // Assert
        Assert.That(result.Select(e => e.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), Is.EqualTo(expected));
    }

    [Test]
    public async Task GetRecordsAsync_ExchangeAheadOfUtc_ReturnsTheFirstDay()
    {
        // A local day in New Zealand starts at noon UTC the day before, so a record stamped at the
        // start of that day lies before a period1 cut at UTC midnight.
        // Arrange
        var firstDay = new DateTime(2024, 5, 13, 0, 0, 0, DateTimeKind.Utc);
        var days = Enumerable.Range(-3, 10).Select(offset => firstDay.AddDays(offset)).Select(day => (day, day.AddDays(1)));
        SetupChartEndpoint(12 * 3600, days);
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var result = await service.GetRecordsAsync("AIR.NZ", firstDay, firstDay.AddDays(2), EYahooInterval.Daily);

        // Assert - exactly the three requested days, newest first
        Assert.That(result.Select(e => e.Date), Is.EqualTo(new[] { firstDay.AddDays(2), firstDay.AddDays(1), firstDay }));
    }

    [Test]
    public async Task GetRecordsAsync_DeepHistory_IsNotTruncated()
    {
        // Daily data is not subject to the intraday retention window, so a 40 year
        // range must go out as a single un-clamped request.
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "records.json");
        var requests = SetupHttpResponseCapturingRequests(HttpStatusCode.OK, await File.ReadAllTextAsync(filePath));
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        var startDate = DateTime.UtcNow.Date.AddYears(-40);

        // Act
        await service.GetRecordsAsync("NVDA", startDate);

        // Assert
        Assert.That(requests, Has.Count.EqualTo(1));
        // unclamped: it starts at startDate, less at most a day of time-zone padding
        var requestedPeriod1 = ReadQueryNumber(requests[0], "period1");
        Assert.That(requestedPeriod1, Is.InRange(Helper.ToUnixTime(startDate.AddDays(-1)).Value, Helper.ToUnixTime(startDate).Value));
    }

    [Test]
    public void GetRecordsAsync_NoResponse_Throws()
    {
        // Arrange
        SetupHttpResponseCapturingRequests(HttpStatusCode.OK, "");
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act + Assert
        Assert.ThrowsAsync<FinanceNetException>(async () => await service.GetRecordsAsync("IBM", default));
    }

    [Test]
    public void GetRecordsAsync_EmptyResponse_Throws()
    {
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "empty.json");
        SetupHttpJsonFileResponse(filePath);
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act + Assert
        Assert.ThrowsAsync<FinanceNetNoDataException>(async () => await service.GetRecordsAsync("IBM", default));
    }

    [Test]
    public void GetRecordsAsync_NoRecordsInPeriod_ThrowsNoData()
    {
        // A range with no trading day in it (a weekend, or today for a mutual fund
        // whose NAV is not out yet) comes back with the correct shape and no bars.
        // That is a correct answer with no rows, not a broken response.
        SetupHttpResponseCapturingRequests(
            HttpStatusCode.OK,
            "{\"chart\":{\"result\":[{\"meta\":{\"symbol\":\"FAVQX\",\"gmtoffset\":0}," +
            "\"timestamp\":[],\"indicators\":{\"quote\":[{}]}}],\"error\":null}}");
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        var exception = Assert.ThrowsAsync<FinanceNetNoDataException>(async () => await service.GetRecordsAsync("FAVQX", new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc)));

        Assert.That(exception.Message, Does.Contain("FAVQX"));
        Assert.That(exception.InnerException, Is.Null);
    }
    [Test]
    public void GetInstrumentsAsync_NoResponse_Throws()
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("", Encoding.UTF8, "text/html"),
            });
        _mockHttpClientFactory.Setup(e => e.CreateClient(It.IsAny<string>())).Returns(new HttpClient(_mockHandler.Object));

        // Arrange		
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var exception = Assert.ThrowsAsync<FinanceNetException>(async () => await service.GetInstrumentsAsync());
        Assert.That(exception, Is.Not.InstanceOf<FinanceNetNoDataException>(), "a transport failure is not a permanent no-data answer");
        Assert.That(exception.Message, Does.Contain("No instruments found"));
    }

    [Test]
    public void GetInstrumentsAsync_EmptyResponse_Throws()
    {
        // Arrange		
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "empty.html");
        SetupHttpHtmlFileResponse(filePath);
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var exception = Assert.ThrowsAsync<FinanceNetException>(async () => await service.GetInstrumentsAsync());
        Assert.That(exception, Is.Not.InstanceOf<FinanceNetNoDataException>(), "a transport failure is not a permanent no-data answer");
        Assert.That(exception.Message, Does.Contain("No instruments found"));
    }

    [TestCase(EInstrumentType.Index, 41)]
    [TestCase(EInstrumentType.Stock, 25)]
    [TestCase(EInstrumentType.ETF, 25)]
    [TestCase(EInstrumentType.Forex, 23)]
    [TestCase(EInstrumentType.Crypto, 25)]
    [TestCase(null, 139)]
    public async Task GetInstrumentsAsync_WithResponse_ReturnsResult(EInstrumentType? type, int expectedCnt)
    {
        // Arrange
        var filePathIndex = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "symbols_indices.html");
        var filePathStocks = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "symbols_stocks.html");
        var filePathEtfs = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "symbols_etfs.html");
        var filePathForex = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "symbols_forex.html");
        var filePathCryptos = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "symbols_cryptos.html");

        if (type == null)
        {
            SetupHttpHtmlFileResponses([filePathStocks, filePathEtfs, filePathCryptos, filePathIndex, filePathForex]);
        }
        else
        {
            var filePath = type switch
            {
                EInstrumentType.Index => filePathIndex,
                EInstrumentType.Stock => filePathStocks,
                EInstrumentType.ETF => filePathEtfs,
                EInstrumentType.Forex => filePathForex,
                EInstrumentType.Crypto => filePathCryptos,
                _ => throw new NotImplementedException(),
            };
            SetupHttpHtmlFileResponse(filePath);
        }

        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var result = await service.GetInstrumentsAsync(type);

        // Assert
        Assert.That(result.Count(), Is.EqualTo(expectedCnt));
    }

    [Test]
    public async Task GetIntradayRecordsAsync_WithResponse_ReturnsResult()
    {
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "intraday_records.json");
        SetupHttpJsonFileResponse(filePath);

        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var result = (await service.GetIntradayRecordsAsync(
            "IBM",
            new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc),
            EInterval.Interval_15Min)).ToList();

        // Assert
        Assert.That(result, Is.Not.Empty);
        Assert.That(result.All(e => e.Open > 0 && e.High > 0 && e.Low > 0 && e.Close > 0));
        Assert.That(result.All(e => e.High >= e.Low));
        Assert.That(result.Any(e => e.Volume > 0));

        // sub-daily granularity: more than one record per calendar day
        Assert.That(result.Count, Is.GreaterThan(result.Select(e => e.DateTime.Date).Distinct().Count()));
    }

    [Test]
    public async Task GetIntradayRecordsAsync_ResponseHasBarsBeyondEndDate_ExcludesThem()
    {
        // Yahoo appends the current partial bar to the chart response even when it lies
        // outside the requested period2 - the fixture contains such a bar (2026-08-05).
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "intraday_records.json");
        SetupHttpJsonFileResponse(filePath);

        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var result = (await service.GetIntradayRecordsAsync(
            "IBM",
            new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc),
            EInterval.Interval_15Min)).ToList();

        // Assert
        Assert.That(result.Select(e => e.DateTime.Date), Is.All.InRange(new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(result.Any(e => e.DateTime.Date == new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc)), Is.False);
    }

    [Test]
    public void GetIntradayRecordsAsync_NoResponse_Throws()
    {
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "empty.json");
        SetupHttpJsonFileResponse(filePath);

        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act + Assert
        Assert.ThrowsAsync<FinanceNetNoDataException>(async () => await service.GetIntradayRecordsAsync(
            "IBM",
            new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc),
            EInterval.Interval_15Min));
    }

    [Test]
    public void GetIntradayRecordsAsync_StartAfterEnd_Throws()
    {
        // Arrange
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act + Assert
        Assert.ThrowsAsync<FinanceNetException>(async () => await service.GetIntradayRecordsAsync(
            "IBM",
            new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc),
            EInterval.Interval_15Min));
    }

    [Test]
    public void GetIntradayRecordsAsync_UnprocessableEntity_ThrowsWithoutRetrying()
    {
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "intraday_records_out_of_range.json");
        var requests = SetupHttpResponseCapturingRequests(HttpStatusCode.UnprocessableEntity, File.ReadAllText(filePath));

        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            CreateRealPolicyRegistry(),
            _mockYahooSession.Object);

        // Act
        var exception = Assert.ThrowsAsync<FinanceNetInvalidRequestException>(async () => await service.GetIntradayRecordsAsync(
            "MSFT",
            DateTime.UtcNow.AddDays(-5).Date,
            null,
            EInterval.Interval_60Min));

        // Assert - Yahoo's own explanation is surfaced, and the call is not retried
        Assert.That(exception.Message, Does.Contain("730 days"));
        Assert.That(requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetIntradayRecordsAsync_RangeExceedsMaxSpanPerRequest_SplitsIntoChunks()
    {
        // Yahoo serves at most 8 days of 1m data per request, so a 20 day range needs several.
        // Arrange - bars are generated onto the days the range covers, see BuildIntradayChartJson
        var startDate = DateTime.UtcNow.AddDays(-20).Date;
        var requests = SetupHttpResponseCapturingRequests(HttpStatusCode.OK, BuildIntradayChartJson(startDate, 21));

        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        await service.GetIntradayRecordsAsync(
            "MSFT",
            startDate,
            DateTime.UtcNow.Date,
            EInterval.Interval_1Min);

        // Assert
        Assert.That(requests, Has.Count.GreaterThan(1));
        Assert.That(requests, Is.All.Contain("interval=1m"));
    }

    [TestCase(12, 10, 17, EInterval.Interval_1Min)]   // NZX: the session opens before UTC midnight
    [TestCase(-4, 0, 24, EInterval.Interval_1Min)]    // futures in New York: the evening runs past UTC midnight
    [TestCase(12, 10, 17, EInterval.Interval_15Min)]  // one request, so only the range edges are at stake
    [TestCase(-4, 0, 24, EInterval.Interval_15Min)]
    public async Task GetIntradayRecordsAsync_LocalDayCrossesUtcMidnight_ReturnsEveryBarInRange(int gmtOffsetHours, int openHour, int closeHour, EInterval interval)
    {
        // Records are grouped by the exchange's local date, which does not start at UTC midnight.
        // No bar of a requested local day may be lost - at the range edges or between chunks.
        // Arrange
        var firstDay = DateTime.UtcNow.Date.AddDays(-12);
        var lastDay = firstDay.AddDays(9);
        var bars = Enumerable.Range(-2, 14)
            .SelectMany(day => Enumerable.Range(openHour, closeHour - openHour).Select(hour => firstDay.AddDays(day).AddHours(hour)))
            .ToList();
        SetupChartEndpoint(gmtOffsetHours * 3600, bars.Select(bar => (bar, bar.AddMinutes(1))));
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        var result = await service.GetIntradayRecordsAsync("X", firstDay, lastDay, interval);

        // Assert
        Assert.That(result.Select(e => e.DateTime), Is.EquivalentTo(bars.Where(bar => bar.Date >= firstDay && bar.Date <= lastDay)));
    }

    [Test]
    public async Task GetIntradayRecordsAsync_OneMinuteChunks_StayWithinYahoosPerRequestSpan()
    {
        // Yahoo rejects a 1m request spanning more than 8 days, whatever the request is padded by.
        // Arrange
        var firstDay = DateTime.UtcNow.Date.AddDays(-25);
        var requests = SetupHttpResponseCapturingRequests(HttpStatusCode.OK, BuildIntradayChartJson(firstDay, 26));
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        await service.GetIntradayRecordsAsync("MSFT", firstDay, DateTime.UtcNow.Date, EInterval.Interval_1Min);

        // Assert
        var spans = requests.Select(url => TimeSpan.FromSeconds(ReadQueryNumber(url, "period2") - ReadQueryNumber(url, "period1")));
        Assert.That(spans, Is.All.LessThan(TimeSpan.FromDays(8)));
    }

    [Test]
    public async Task GetIntradayRecordsAsync_StartBeyondRetention_ClampsToWhatYahooKeeps()
    {
        // Arrange - bars land just inside the retention boundary the service should clamp to
        var requests = SetupHttpResponseCapturingRequests(
            HttpStatusCode.OK,
            BuildIntradayChartJson(DateTime.UtcNow.Date.AddDays(-729), 30));

        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act - 10 years of hourly data, of which Yahoo only keeps the last 730 days
        await service.GetIntradayRecordsAsync(
            "MSFT",
            DateTime.UtcNow.AddYears(-10).Date,
            DateTime.UtcNow.Date,
            EInterval.Interval_60Min);

        // Assert - the earliest period1 asked for is the retention boundary, not 10 years back,
        // and no padding reaches past it: the service stays a day inside Yahoo's 730
        var earliestAllowed = Helper.ToUnixTime(DateTime.UtcNow.Date.AddDays(-729));
        var requestedPeriod1 = requests
            .Select(url => long.Parse(Regex.Match(url, @"period1=(\d+)").Groups[1].Value, CultureInfo.InvariantCulture))
            .Min();
        Assert.That(requestedPeriod1, Is.GreaterThanOrEqualTo(earliestAllowed));

        _mockLogger.Verify(
            logger => logger.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString().Contains("730")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce);
    }

    [Test]
    public void GetIntradayRecordsAsync_RangeCompletelyBeyondRetention_ThrowsWithoutCallingYahoo()
    {
        // Arrange
        var requests = SetupHttpResponseCapturingRequests(HttpStatusCode.OK, "{}");

        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act - the whole window sits outside the 30 day retention of 1m data
        var exception = Assert.ThrowsAsync<FinanceNetInvalidRequestException>(async () => await service.GetIntradayRecordsAsync(
            "MSFT",
            DateTime.UtcNow.AddDays(-500).Date,
            DateTime.UtcNow.AddDays(-400).Date,
            EInterval.Interval_1Min));

        // Assert
        Assert.That(exception.Message, Does.Contain("30"));
        Assert.That(requests, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UnknownSymbol_ThrowsNoDataWithoutRetrying(bool intraday)
    {
        // Yahoo answers a symbol it does not know with a 404 - a permanent answer, not an outage.
        // A multi-chunk intraday range must stop at the first request rather than ask for every chunk.
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "chart_unknown_symbol.json");
        var requests = SetupHttpResponseCapturingRequests(HttpStatusCode.NotFound, File.ReadAllText(filePath));
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            CreateRealPolicyRegistry(),
            _mockYahooSession.Object);

        // Act
        var exception = Assert.ThrowsAsync<FinanceNetNoDataException>(async () =>
        {
            if (intraday)
            {
                await service.GetIntradayRecordsAsync("TESTING.NET", DateTime.UtcNow.Date.AddDays(-20), null, EInterval.Interval_1Min);
            }
            else
            {
                await service.GetRecordsAsync("TESTING.NET", DateTime.UtcNow.Date.AddDays(-20));
            }
        });

        // Assert - Yahoo's explanation is surfaced, unwrapped, from a single request
        Assert.That(exception.Message, Does.Contain("TESTING.NET").And.Contain("may be delisted"));
        Assert.That(exception.InnerException, Is.Null);
        Assert.That(requests, Has.Count.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BadRequest_ThrowsInvalidRequestWithoutRetrying(bool intraday)
    {
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "chart_bad_request.json");
        var requests = SetupHttpResponseCapturingRequests(HttpStatusCode.BadRequest, File.ReadAllText(filePath));
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            CreateRealPolicyRegistry(),
            _mockYahooSession.Object);

        // Act
        var exception = Assert.ThrowsAsync<FinanceNetInvalidRequestException>(async () =>
        {
            if (intraday)
            {
                await service.GetIntradayRecordsAsync("MSFT", DateTime.UtcNow.Date.AddDays(-5), null, EInterval.Interval_15Min);
            }
            else
            {
                await service.GetRecordsAsync("MSFT", DateTime.UtcNow.Date.AddDays(-5));
            }
        });

        // Assert
        Assert.That(exception.Message, Does.Contain("start date cannot be after end date"));
        Assert.That(requests, Has.Count.EqualTo(1));
    }

    [TestCase(HttpStatusCode.TooManyRequests)]
    [TestCase(HttpStatusCode.RequestTimeout)]
    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.InternalServerError)]
    public void TransientRejection_IsRetried(HttpStatusCode statusCode)
    {
        // Rate limiting, timeouts and an expired session can clear up - only the rejections that
        // describe the request itself are final. 401 stays retryable as a stale session looks like it.
        // Arrange
        var requests = SetupHttpResponseCapturingRequests(statusCode, "");
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            CreateRealPolicyRegistry(),
            _mockYahooSession.Object);

        // Act
        var exception = Assert.ThrowsAsync<FinanceNetException>(async () => await service.GetRecordsAsync("MSFT", DateTime.UtcNow.Date.AddDays(-5)));

        // Assert - the first attempt and the policy's three retries
        Assert.That(exception.InnerException, Is.Not.Null);
        Assert.That(requests, Has.Count.EqualTo(4));
    }

    private IReadOnlyPolicyRegistry<string> CreateRealPolicyRegistry()
    {
        var registry = new Mock<IReadOnlyPolicyRegistry<string>>();
        var policy = PollyPolicyFactory.GetRetryPolicy(3, 0, _mockLogger.Object);
        registry.Setup(e => e.Get<AsyncPolicy>(Constants.DefaultHttpRetryPolicy)).Returns(policy);
        registry.Setup(e => e.Get<IAsyncPolicy>(Constants.DefaultHttpRetryPolicy)).Returns(policy);
        return registry.Object;
    }

    [Test]
    public void GetIntradayRecordsAsync_StartInFutureWithoutEndDate_ThrowsArgumentError()
    {
        // Omitting endDate must not skip the range check - the caller's mistake should be
        // named, not reported as "Yahoo returned no intraday records".
        SetupHttpResponseCapturingRequests(HttpStatusCode.OK, BuildIntradayChartJson(DateTime.UtcNow.Date.AddDays(-5), 6));
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        var exception = Assert.ThrowsAsync<FinanceNetException>(async () =>
            await service.GetIntradayRecordsAsync("MSFT", DateTime.UtcNow.Date.AddDays(1)));

        Assert.That(exception, Is.Not.InstanceOf<FinanceNetNoDataException>());
        Assert.That(exception.Message, Does.Contain("startDate"));
    }

    [Test]
    public void GetIntradayRecordsAsync_UnsupportedInterval_ThrowsFinanceNetException()
    {
        // Every failure the library surfaces is a FinanceNetException, including a bad argument.
        SetupHttpResponseCapturingRequests(HttpStatusCode.OK, BuildIntradayChartJson(DateTime.UtcNow.Date.AddDays(-5), 6));
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        Assert.ThrowsAsync<FinanceNetException>(async () =>
            await service.GetIntradayRecordsAsync(
                "MSFT",
                DateTime.UtcNow.Date.AddDays(-1),
                DateTime.UtcNow.Date,
                (EInterval)99));
    }

    /// <summary>
    /// Builds a chart payload whose bars sit on the days the test is about to request.
    /// </summary>
    /// <remarks>
    /// A fixture with fixed timestamps rots. The service measures Yahoo's retention window
    /// from <see cref="DateTime.UtcNow"/>, so bars written down once eventually fall outside
    /// every range it will accept - first the range under test, then the retention guard.
    /// Generating them keeps the data and the requested window moving together.
    /// The offset is zero so exchange-local dates equal the UTC dates the test asks for.
    /// </remarks>
    private static string BuildIntradayChartJson(DateTime firstBarDate, int days)
    {
        var timestamps = Enumerable.Range(0, days)
            .Select(day => Helper.ToUnixTime(firstBarDate.Date.AddDays(day).AddHours(14)).Value);
        return BuildChartJson(timestamps, 0);
    }

    private static string BuildChartJson(IEnumerable<long> timestamps, int gmtOffsetSeconds, string events = null)
    {
        var stamps = timestamps.Select(unixTime => unixTime.ToString(CultureInfo.InvariantCulture)).ToList();
        var prices = string.Join(",", stamps.Select(_ => "1.0"));
        var volumes = string.Join(",", stamps.Select(_ => "100"));
        return "{\"chart\":{\"result\":[{"
            + "\"meta\":{\"symbol\":\"MSFT\",\"gmtoffset\":" + gmtOffsetSeconds.ToString(CultureInfo.InvariantCulture) + "},"
            + "\"timestamp\":[" + string.Join(",", stamps) + "],"
            + (events == null ? "" : "\"events\":" + events + ",")
            + "\"indicators\":{\"quote\":[{"
            + "\"open\":[" + prices + "],"
            + "\"high\":[" + prices + "],"
            + "\"low\":[" + prices + "],"
            + "\"close\":[" + prices + "],"
            + "\"volume\":[" + volumes + "]}]}}],"
            + "\"error\":null}}";
    }

    private const int NewYorkSummerOffset = -4 * 3600;

    private static long NewYorkToUnix(int year, int month, int day, int hour = 0, int minute = 0)
        => Helper.ToUnixTime(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc)).Value - NewYorkSummerOffset;

    /// <summary>
    /// A chart events block as Yahoo sends it: each action carries the moment it happened (the open,
    /// 09:30 local) as its date. The keys are never read, so each action is keyed by its own date.
    /// </summary>
    private static string BuildEventsJson(IEnumerable<(long Date, double Amount)> dividends, IEnumerable<(long Date, int Numerator, int Denominator)> splits)
    {
        var dividendEntries = dividends.Select(d =>
            $"\"{d.Date}\":{{\"amount\":{d.Amount.ToString(CultureInfo.InvariantCulture)},\"date\":{d.Date}}}");
        var splitEntries = splits.Select(e =>
            $"\"{e.Date}\":{{\"date\":{e.Date},\"numerator\":{e.Numerator},\"denominator\":{e.Denominator},\"splitRatio\":\"{e.Numerator}:{e.Denominator}\"}}");
        return "{\"dividends\":{" + string.Join(",", dividendEntries) + "},\"splits\":{" + string.Join(",", splitEntries) + "}}";
    }

    /// <summary>
    /// Stands in for the chart endpoint: each request is answered with the bars Yahoo would
    /// serve for its period1/period2, out of a fixed set given in exchange-local time.
    /// </summary>
    /// <param name="gmtOffsetSeconds">The exchange's offset from UTC.</param>
    /// <param name="localBars">Each bar's period, in exchange-local time.</param>
    /// <param name="servesWholePeriods">
    /// Yahoo is not consistent here. Intraday and monthly bars come back only when their stamp lies in
    /// [period1, period2); a weekly bar comes back whenever its week overlaps that range, even when it
    /// is stamped before period1.
    /// </param>
    private void SetupChartEndpoint(int gmtOffsetSeconds, IEnumerable<(DateTime Start, DateTime End)> localBars, bool servesWholePeriods = false)
    {
        var bars = localBars
            .Select(bar => (Start: Helper.ToUnixTime(bar.Start).Value - gmtOffsetSeconds, End: Helper.ToUnixTime(bar.End).Value - gmtOffsetSeconds))
            .OrderBy(bar => bar.Start)
            .ToList();
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var url = request.RequestUri.ToString();
                var period1 = ReadQueryNumber(url, "period1");
                var period2 = ReadQueryNumber(url, "period2");
                var served = bars
                    .Where(bar => servesWholePeriods
                        ? bar.Start < period2 && bar.End > period1
                        : bar.Start >= period1 && bar.Start < period2)
                    .Select(bar => bar.Start);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(BuildChartJson(served, gmtOffsetSeconds), Encoding.UTF8, "application/json"),
                };
            });
        _mockHttpClientFactory.Setup(e => e.CreateClient(It.IsAny<string>())).Returns(new HttpClient(_mockHandler.Object));
    }

    private static long ReadQueryNumber(string url, string name)
        => long.Parse(Regex.Match(url, $@"[?&]{name}=(-?\d+)").Groups[1].Value, CultureInfo.InvariantCulture);

    private List<string> SetupHttpResponseCapturingRequests(HttpStatusCode statusCode, string content)
    {
        var requests = new List<string>();
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => requests.Add(request.RequestUri.ToString()))
            .ReturnsAsync(() => new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            });
        _mockHttpClientFactory.Setup(e => e.CreateClient(It.IsAny<string>())).Returns(new HttpClient(_mockHandler.Object));
        return requests;
    }

    private void SetupHttpHtmlFileResponse(string filePath)
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(File.ReadAllText(filePath), Encoding.UTF8, "text/html"),
                Headers =
                {
                    { "Set-Cookie", "\"A3=d=AQABBIPiUmcCEKLS0S2dxFEvSY2wq0BTJc4FEgEBAQE0VGdcZ-AMyiMA_eMAAA&S=AQAAAueeOka9YBgG-7Z2662G2t0; Expires=Mo, 10 Dec 2040 17:39:47 GMT; Max-Age=99931557600; Domain=.yahoo.com; Path=/; SameSite=None; Secure; HttpOnly\"" } // Add custom headers here
                            }
            });
        _mockHttpClientFactory.Setup(e => e.CreateClient(It.IsAny<string>())).Returns(new HttpClient(_mockHandler.Object));
    }

    private void SetupHttpHtmlFileResponses(List<string> filePaths)
    {
        var setupSequence = _mockHandler
            .Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            );

        foreach (var filePath in filePaths)
        {
            var content = File.ReadAllText(filePath);
            setupSequence = setupSequence.ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "text/html"),
                Headers =
            {
                { "Set-Cookie", "\"A3=d=AQABBIPiUmcCEKLS0S2dxFEvSY2wq0BTJc4FEgEBAQE0VGdcZ-AMyiMA_eMAAA&S=AQAAAueeOka9YBgG-7Z2662G2t0; Expires=Mo, 10 Dec 2040 17:39:47 GMT; Max-Age=99931557600; Domain=.yahoo.com; Path=/; SameSite=None; Secure; HttpOnly\"" }
            }
            });
        }
        _mockHttpClientFactory.Setup(e => e.CreateClient(It.IsAny<string>())).Returns(new HttpClient(_mockHandler.Object));
    }

    private void SetupHttpJsonFileResponse(string filePath)
    {
        var jsonContent = File.ReadAllText(filePath);
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonContent, Encoding.UTF8, "text/html"),
                Headers =
                {
                    { "Set-Cookie", "\"A3=d=AQABBIPiUmcCEKLS0S2dxFEvSY2wq0BTJc4FEgEBAQE0VGdcZ-AMyiMA_eMAAA&S=AQAAAueeOka9YBgG-7Z2662G2t0; Expires=Mo, 10 Dec 2040 17:39:47 GMT; Max-Age=99931557600; Domain=.yahoo.com; Path=/; SameSite=None; Secure; HttpOnly\"" } // Add custom headers here
                            }
            });
        _mockHttpClientFactory.Setup(e => e.CreateClient(It.IsAny<string>())).Returns(new HttpClient(_mockHandler.Object));
    }
}

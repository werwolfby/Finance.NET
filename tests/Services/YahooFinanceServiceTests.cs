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
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "records.html");
        SetupHttpHtmlFileResponse(filePath);
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        DateTime startDate = default;

        // Act
        var result = await service.GetRecordsAsync("IBM", startDate);

        // Assert
        Assert.That(result, Is.Not.Empty);
        Assert.That(result.All(e => e.Open != null));
        Assert.That(result.All(e => e.Close != null));
        Assert.That(result.All(e => e.AdjustedClose != null));
        Assert.That(result.All(e => e.Low != null));
        Assert.That(result.All(e => e.High != null));
    }
    [Test]
    public void GetRecordsAsync_NoResponse_Throws()
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

        DateTime startDate = default;

        // Act
        var exception = Assert.ThrowsAsync<FinanceNetException>(async () => await service.GetRecordsAsync("IBM", startDate));
        Assert.That(exception.InnerException.Message, Does.Contain("received 200, but no html content"));
    }
    [Test]
    public void GetRecordsAsync_EmptyResponse_Throws()
    {
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "empty.html");
        SetupHttpHtmlFileResponse(filePath);
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        DateTime startDate = default;

        // Act
        var exception = Assert.ThrowsAsync<FinanceNetException>(async () => await service.GetRecordsAsync("IBM", startDate));
        Assert.That(exception.InnerException.Message, Does.Contain("table is null"));
    }

    [Test]
    public void GetRecordsAsync_NoRecordsInPeriod_ThrowsNoData()
    {
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "records_empty_period.html");
        SetupHttpHtmlFileResponse(filePath);
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
    public void GetRecordsAsync_MissingHeadersWithoutErrorCell_Throws()
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<html><body><table class=\"table\"><thead><tr><th>Date</th><th>Open</th></tr></thead>" +
                    "<tbody><tr><td>Jan 2, 2026</td><td>1.00</td></tr></tbody></table></body></html>",
                    Encoding.UTF8,
                    "text/html"),
            });
        _mockHttpClientFactory.Setup(e => e.CreateClient(It.IsAny<string>())).Returns(new HttpClient(_mockHandler.Object));
        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        var exception = Assert.ThrowsAsync<FinanceNetException>(async () => await service.GetRecordsAsync("IBM", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        Assert.That(exception.InnerException.Message, Does.Contain("Headers are missing"));
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
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "intraday_records.json");
        var requests = SetupHttpResponseCapturingRequests(HttpStatusCode.OK, await File.ReadAllTextAsync(filePath));

        var service = new YahooFinanceService(
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPolicyRegistry.Object,
            _mockYahooSession.Object);

        // Act
        await service.GetIntradayRecordsAsync(
            "MSFT",
            DateTime.UtcNow.AddDays(-20).Date,
            DateTime.UtcNow.Date,
            EInterval.Interval_1Min);

        // Assert
        Assert.That(requests, Has.Count.GreaterThan(1));
        Assert.That(requests, Is.All.Contain("interval=1m"));
    }

    [Test]
    public async Task GetIntradayRecordsAsync_StartBeyondRetention_ClampsToWhatYahooKeeps()
    {
        // Arrange
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Yahoo", "intraday_records.json");
        var requests = SetupHttpResponseCapturingRequests(HttpStatusCode.OK, await File.ReadAllTextAsync(filePath));

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

        // Assert - the earliest period1 asked for is the retention boundary, not 10 years back
        var earliestAllowed = Helper.ToUnixTime(DateTime.UtcNow.Date.AddDays(-730));
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

    private IReadOnlyPolicyRegistry<string> CreateRealPolicyRegistry()
    {
        var registry = new Mock<IReadOnlyPolicyRegistry<string>>();
        var policy = PollyPolicyFactory.GetRetryPolicy(3, 0, _mockLogger.Object);
        registry.Setup(e => e.Get<AsyncPolicy>(Constants.DefaultHttpRetryPolicy)).Returns(policy);
        registry.Setup(e => e.Get<IAsyncPolicy>(Constants.DefaultHttpRetryPolicy)).Returns(policy);
        return registry.Object;
    }

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

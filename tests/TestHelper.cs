using Finance.Net.Extensions;
using Finance.Net.Tests.IntegrationTests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Finance.Net.Tests;

internal static class TestHelper
{
    public static ServiceProvider SetUpServiceProvider()
    {
        JsonConvert.DefaultSettings = () => new JsonSerializerSettings
        {
            Converters = { new StringEnumConverter() },
            Formatting = Formatting.Indented
        };

        var services = new ServiceCollection();
        var cfgBuilder = new ConfigurationBuilder();
        cfgBuilder.AddUserSecrets<AlphaVantageTests>();
        cfgBuilder.AddEnvironmentVariables();
        var cfg = cfgBuilder.Build();

        services.AddSingleton<IConfiguration>(cfg);
        services.AddFinanceNet(new FinanceNetConfiguration
        {
            HttpTimeout = 3,
            HttpRetryCount = 10,
            AlphaVantageApiKey = cfg["FinanceNet:AlphaVantageApiKey"]
        });
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
            builder.AddFilter("Finance.Net", LogLevel.Information);

            builder.AddSimpleConsole(options =>
            {
                options.UseUtcTimestamp = true;
                options.SingleLine = true;
                options.ColorBehavior = Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Enabled;
            });
        });
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The local date of the most recent daylight-saving change in <paramref name="zone"/> - the
    /// first day whose midday offset differs from the day before's.
    /// </summary>
    public static System.DateTime LastDaylightSavingChange(System.TimeZoneInfo zone)
    {
        var day = System.DateTime.SpecifyKind(System.DateTime.UtcNow.Date.AddDays(-2), System.DateTimeKind.Unspecified);
        for (var i = 0; i < 400; i++, day = day.AddDays(-1))
        {
            if (zone.GetUtcOffset(day.AddHours(12)) != zone.GetUtcOffset(day.AddDays(-1).AddHours(12)))
            {
                return day;
            }
        }
        throw new System.InvalidOperationException($"{zone.Id} has not changed its clocks in the last 400 days");
    }
}

using System.Net;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SpeakerPipeline.Telegram.Tests;

public sealed class TelegramCredentialLoggingTests
{
    private const string SyntheticToken = "123456789:SYNTHETIC_DO_NOT_LOG";
    [Fact]
    public async Task Default_HTTP_client_handlers_cannot_log_the_bot_credential()
    {
        var logs = new CaptureLogs();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Telegram:BotToken"] = SyntheticToken, ["Telegram:ChatId"] = "7", ["Telegram:Enabled"] = "true" }).Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddTelegram(configuration);
        services.AddHttpClient<TelegramClient>().ConfigurePrimaryHttpMessageHandler(() => new Response());
        using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<TelegramClient>().SendAsync(7, "Synthetic test", null, true, default);
        Assert.DoesNotContain(logs.Lines, line => line.Contains("SYNTHETIC_DO_NOT_LOG", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, line => line.Contains("api.telegram.org/bot", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_exceptions_and_provider_response_bodies_do_not_escape(bool transportFailure)
    {
        var logs = new CaptureLogs();
        var options = Microsoft.Extensions.Options.Options.Create(new TelegramOptions { BotToken = SyntheticToken });
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var client = new TelegramClient(new HttpClient(new Failure(transportFailure)), options, loggerFactory.CreateLogger<TelegramClient>());
        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.SendAsync(7, "Test", null, false, default));
        Assert.DoesNotContain(SyntheticToken, error.ToString());
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(logs.Lines, line => line.Contains(SyntheticToken, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://api.telegram.org/bot123456789:SYNTHETIC_DO_NOT_LOG/sendMessage")]
    [InlineData("https://customsearch.googleapis.com/customsearch/v1?key=SYNTHETIC_DO_NOT_LOG&cx=private&q=topic")]
    public void Span_urls_and_exception_status_never_contain_credentials(string uri)
    {
        using var activity = new Activity("outbound").Start();
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        activity.SetTag("url.full", uri);
        activity.SetTag("http.url", uri);
        activity.SetTag("url.query", "key=SYNTHETIC_DO_NOT_LOG");
        activity.SetTag("exception.message", uri);
        activity.SetTag("exception.stacktrace", uri);
        activity.SetStatus(ActivityStatusCode.Error, uri);
        OutboundHttpPrivacy.EnrichRequest(activity, request);
        OutboundHttpPrivacy.EnrichException(activity, new HttpRequestException(uri));
        Assert.DoesNotContain("SYNTHETIC_DO_NOT_LOG", activity.DisplayName);
        Assert.DoesNotContain("SYNTHETIC_DO_NOT_LOG", activity.StatusDescription);
        Assert.DoesNotContain(activity.TagObjects, tag => tag.Value?.ToString()?.Contains("SYNTHETIC_DO_NOT_LOG", StringComparison.Ordinal) == true);
        Assert.Empty(activity.Events);
    }

    private sealed class Failure(bool transportFailure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (transportFailure) throw new HttpRequestException($"Synthetic transport failed at {request.RequestUri}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent($"{{\"ok\":false,\"description\":\"Provider echoed {SyntheticToken}\"}}") });
        }
    }

    private sealed class Response : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":42,\"chat\":{\"id\":7}}}") });
    }

    private sealed class CaptureLogs : ILoggerProvider, ILogger
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception) + exception);
    }
}

using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Rasel.Application.Ports;
using Rasel.Infrastructure.Configuration;
using Rasel.Infrastructure.Providers.Bridge;

namespace Rasel.Infrastructure.Tests;

public class LoggingLeakTests
{
    private sealed class Capture : ILoggerProvider, ILogger
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add(formatter(state, exception));
        }
        public void Dispose() { }
    }

    private sealed class Ok : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("SendDone") });
    }

    [Fact]
    public async Task Bridge_phone_send_never_logs_the_phone_number_or_the_message_text()
    {
        var capture = new Capture();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WhatsApp:Provider"] = "Bridge", ["WhatsApp:Bridge:BaseUrl"] = "http://127.0.0.1:5001", ["WhatsApp:Bridge:ReadRetries"] = "0",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));
        services.AddOptions<WhatsAppOptions>().Bind(config.GetSection("WhatsApp"));
        services.AddBridgeProvider();
        services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new Ok()));

        await services.BuildServiceProvider().GetRequiredService<IWhatsAppProvider>()
            .SendTextToPhoneAsync(Guid.NewGuid(), "+201001234567", "Your code is 987654", Guid.NewGuid(), default);

        var all = string.Join("\n", capture.Lines);
        Assert.DoesNotContain("201001234567", all);
        Assert.DoesNotContain("987654", all);
    }
}

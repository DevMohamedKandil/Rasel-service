namespace Rasel.Infrastructure.Configuration;

public sealed class DatabaseOptions
{
    public string Path { get; set; } = "rasel.db";

    /// <summary>Apply pending EF Core migrations at startup. Disable when migrations are run by the deployment pipeline.</summary>
    public bool MigrateOnStartup { get; set; } = true;
}

public sealed class SecurityOptions
{
    /// <summary>Server-side secret mixed into every OTP hash (HMAC key). Must never be stored next to the database.</summary>
    public string OtpPepper { get; set; } = "";

    /// <summary>HS256 key used to sign the verification token handed to calling projects.</summary>
    public string TokenSecret { get; set; } = "";
}

public sealed class WebhookOptions
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public string Secret { get; set; } = "";
}

public sealed class WhatsAppOptions
{
    public const string FakeProvider = "Fake";
    public const string WebJsProvider = "WebJs";
    public const string BridgeProvider = "Bridge";

    /// <summary>"Fake" (in-memory, default), "Bridge" (the real existing whatsapp-web.js bridge) or "WebJs" (invented sidecar contract).</summary>
    public string Provider { get; set; } = FakeProvider;

    public WebJsOptions WebJs { get; set; } = new();

    public BridgeOptions Bridge { get; set; } = new();
}

public sealed class BridgeOptions
{
    /// <summary>Address of the whatsapp-web.js bridge, e.g. http://127.0.0.1:5001 (the bridge has no authentication: keep it loopback/private).</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>A real send drives a headless browser, so this is more generous than for the sidecar.</summary>
    public int AttemptTimeoutSeconds { get; set; } = 30;

    public int ReadRetries { get; set; } = 2;
    public int BreakerMinimumThroughput { get; set; } = 5;
    public double BreakerFailureRatio { get; set; } = 0.5;
    public int BreakerSamplingSeconds { get; set; } = 30;
    public int BreakerBreakSeconds { get; set; } = 30;
}

public sealed class WebJsOptions
{
    public string BaseUrl { get; set; } = "";
    public string ApiToken { get; set; } = "";

    /// <summary>Timeout of a single HTTP attempt.</summary>
    public int AttemptTimeoutSeconds { get; set; } = 10;

    /// <summary>Extra retries for idempotent calls (GET/DELETE) only. Sends are never retried here: the outbox owns send retries.</summary>
    public int ReadRetries { get; set; } = 2;

    public int BreakerMinimumThroughput { get; set; } = 5;
    public double BreakerFailureRatio { get; set; } = 0.5;
    public int BreakerSamplingSeconds { get; set; } = 30;
    public int BreakerBreakSeconds { get; set; } = 30;
}

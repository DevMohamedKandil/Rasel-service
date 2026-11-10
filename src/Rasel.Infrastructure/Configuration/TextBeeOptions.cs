namespace Rasel.Infrastructure.Configuration;

/// <summary>Hosted TextBee API configuration. Keep ApiKey in a secret store, never in source control.</summary>
public sealed class TextBeeOptions
{
    public const string SectionName = "Sms:TextBee";
    public const string DefaultBaseUrl = "https://api.textbee.dev/api/v1/";

    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string ApiKey { get; set; } = "";
    public string? DeviceId { get; set; }
    public int TimeoutSeconds { get; set; } = 15;
}

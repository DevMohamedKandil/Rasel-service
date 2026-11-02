namespace Rasel.Infrastructure.Messaging;

public sealed class WebhookOptions
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public string Secret { get; set; } = "";
}

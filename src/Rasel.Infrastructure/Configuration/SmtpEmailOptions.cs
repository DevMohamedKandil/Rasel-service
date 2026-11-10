namespace Rasel.Infrastructure.Configuration;

public sealed class SmtpEmailOptions
{
    public const string SectionName = "Email:Smtp";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromEmail { get; set; } = "";
    public string FromName { get; set; } = "Rasel";
    public bool EnableSsl { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 20;
}

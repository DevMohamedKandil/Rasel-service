using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using Rasel.Application.Ports;
using Rasel.Infrastructure.Configuration;

namespace Rasel.Infrastructure.Providers;

/// <summary>SMTP transport. A successful return means the SMTP server accepted the message, not that it reached the inbox.</summary>
public sealed class SmtpEmailProvider(IOptions<SmtpEmailOptions> options) : IEmailProvider
{
    private readonly SmtpEmailOptions _options = options.Value;

    public async Task SendAsync(string to, string subject, string body, bool isHtml, CancellationToken ct)
    {
        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromEmail, _options.FromName),
            Subject = subject,
            Body = body,
            IsBodyHtml = isHtml
        };
        message.To.Add(new MailAddress(to));

#pragma warning disable SYSLIB0014 // Isolated behind IEmailProvider to avoid coupling application logic to SMTP transport details.
        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.EnableSsl,
            Credentials = new NetworkCredential(_options.Username, _options.Password),
            Timeout = checked(_options.TimeoutSeconds * 1000)
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        try
        {
            await client.SendMailAsync(message, timeout.Token);
        }
        catch (SmtpException ex)
        {
            var transient = ex.StatusCode is SmtpStatusCode.GeneralFailure
                or SmtpStatusCode.MailboxBusy
                or SmtpStatusCode.LocalErrorInProcessing
                or SmtpStatusCode.InsufficientStorage;
            throw new ProviderException("SMTP server failed to accept the email.", isTransient: transient);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException("SMTP email send timed out.", isTransient: true);
        }
#pragma warning restore SYSLIB0014
    }
}

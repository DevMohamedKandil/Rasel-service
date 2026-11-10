using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Rasel.Application.Ports;
using Rasel.Infrastructure.Configuration;

namespace Rasel.Infrastructure.Providers;

/// <summary>
/// Sends one SMS through the hosted TextBee API. A successful response means TextBee accepted
/// the batch; it is not proof that the carrier delivered the SMS to the recipient.
/// </summary>
public sealed class TextBeeSmsProvider(HttpClient http, IOptions<TextBeeOptions> options) : ISmsProvider
{
    private readonly TextBeeOptions _options = options.Value;

    public async Task SendAsync(string phoneE164, string text, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "gateway/send-sms")
        {
            Content = JsonContent.Create(new TextBeeSendRequest
            {
                Recipients = [phoneE164],
                Message = text,
                DeviceId = string.IsNullOrWhiteSpace(_options.DeviceId) ? null : _options.DeviceId
            })
        };
        request.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            throw new ProviderException("TextBee API request failed.", transient: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException("TextBee API request timed out.", transient: true);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
                return;

            var status = (int)response.StatusCode;
            var transient = response.StatusCode == HttpStatusCode.RequestTimeout || status >= 500;
            throw new ProviderException(
                $"TextBee rejected the SMS request (HTTP {status}).",
                transient: transient);
        }
    }

    private sealed class TextBeeSendRequest
    {
        [JsonPropertyName("recipients")]
        public required string[] Recipients { get; init; }

        [JsonPropertyName("message")]
        public required string Message { get; init; }

        [JsonPropertyName("deviceId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DeviceId { get; init; }
    }
}

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Rasel.Application.Common;
using Rasel.Application.Ports;

namespace Rasel.Api;

/// <summary>Maps expected failures to RFC 7807 responses with a stable machine-readable <c>code</c>. Anything else falls through to the generic 500 handler.</summary>
public sealed class AppExceptionHandler(IProblemDetailsService problems, ILogger<AppExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        int status;
        string code, detail;
        int? retryAfter = null;

        switch (exception)
        {
            case AppException app:
                status = app.Kind switch
                {
                    ErrorKind.BadRequest => StatusCodes.Status400BadRequest,
                    ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
                    ErrorKind.NotFound => StatusCodes.Status404NotFound,
                    ErrorKind.Conflict => StatusCodes.Status409Conflict,
                    ErrorKind.Gone => StatusCodes.Status410Gone,
                    ErrorKind.Locked => StatusCodes.Status423Locked,
                    ErrorKind.TooManyRequests => StatusCodes.Status429TooManyRequests,
                    _ => StatusCodes.Status502BadGateway,
                };
                (code, detail, retryAfter) = (app.Code, app.Message, app.RetryAfterSeconds);
                break;
            case ProviderException provider:
                logger.LogWarning("Provider failure surfaced to a request: {Message}", PiiRedactor.Redact(provider.Message));
                (status, code, detail) = (StatusCodes.Status502BadGateway, "provider_unavailable", "The WhatsApp provider is unavailable.");
                retryAfter = provider.IsTransient ? 5 : null;
                break;
            case BadHttpRequestException badRequest:
                (status, code, detail) = (badRequest.StatusCode, "invalid_request", "The request could not be read: malformed body or parameters.");
                break;
            case OperationCanceledException when context.RequestAborted.IsCancellationRequested:
                return true; // the client went away; nothing to write
            default:
                return false;
        }

        context.Response.StatusCode = status;
        if (retryAfter is { } seconds) context.Response.Headers.RetryAfter = seconds.ToString();

        var problem = new ProblemDetails { Status = status, Title = code, Detail = detail };
        problem.Extensions["code"] = code;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem, Exception = exception });
    }
}

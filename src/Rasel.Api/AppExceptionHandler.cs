using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Rasel.Application.Common;

namespace Rasel.Api;

public sealed class AppExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not AppException app) return false;

        var status = app.Kind switch
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
        context.Response.StatusCode = status;
        if (app.RetryAfterSeconds is { } seconds) context.Response.Headers.RetryAfter = seconds.ToString();

        var problem = new ProblemDetails { Status = status, Title = app.Code, Detail = app.Message };
        problem.Extensions["code"] = app.Code;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem, Exception = exception });
    }
}

namespace Rasel.Application.Common;

public enum ErrorKind { BadRequest, Unauthorized, NotFound, Conflict, Gone, Locked, TooManyRequests, BadGateway }

/// <summary>Expected, client-visible failure. The API layer maps it to a ProblemDetails response.</summary>
public sealed class AppException(ErrorKind kind, string code, string detail, int? retryAfterSeconds = null) : Exception(detail)
{
    public ErrorKind Kind { get; } = kind;
    public string Code { get; } = code;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

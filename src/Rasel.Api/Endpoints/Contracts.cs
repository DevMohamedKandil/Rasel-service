namespace Rasel.Api.Endpoints;

public sealed record CreateSessionRequest(string? Name);

public sealed record SendMessageRequest(Guid? SessionId, string? Phone, string? GroupName, string? Text);

public sealed record RequestOtpRequest(string? Phone, string? Purpose, string? Channel);

public sealed record VerifyOtpRequest(Guid ChallengeId, string? Code);

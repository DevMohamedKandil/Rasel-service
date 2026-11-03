namespace Rasel.Application.Common;

/// <summary>
/// Safe, closed set of delivery-failure codes. The code is stored as a "[code] " prefix of Message.Error and exposed as
/// lastErrorCode; the rest of the stored text is free-form provider output and is never exposed.
/// </summary>
public static class ErrorCodes
{
    public const string ProviderError = "provider_error";
    public const string ProviderTransient = "provider_transient_error";
    public const string GroupNotFound = "group_not_found_on_session";
    public const string GroupAmbiguous = "group_ambiguous";
    public const string SessionNotReady = "session_not_ready";
    public const string SendFailed = "send_failed";
    public const string InvalidRequest = "invalid_request";
    public const string Timeout = "provider_timeout";
    public const string SidecarUnreachable = "sidecar_unreachable";

    private static readonly HashSet<string> Known = [GroupNotFound, GroupAmbiguous, SessionNotReady, SendFailed, InvalidRequest, Timeout, SidecarUnreachable];

    public static string Tag(string? code, string text) => code is not null && Known.Contains(code) ? $"[{code}] {text}" : text;

    /// <summary>Extracts the allow-listed code from a stored error; null when there is none.</summary>
    public static string? Parse(string? stored)
    {
        if (stored is null || stored.Length < 3 || stored[0] != '[') return null;
        var end = stored.IndexOf(']');
        if (end < 0) return null;
        var code = stored[1..end];
        return Known.Contains(code) ? code : null;
    }
}

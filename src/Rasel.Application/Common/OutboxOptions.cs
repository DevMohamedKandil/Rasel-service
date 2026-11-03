namespace Rasel.Application.Common;

public sealed class OutboxOptions
{
    /// <summary>How often the worker polls for due messages when the outbox is idle.</summary>
    public int PollIntervalMs { get; set; } = 500;

    /// <summary>Total attempts (first try included) before a transient failure becomes terminal.</summary>
    public int MaxAttempts { get; set; } = 5;
    public int BaseBackoffSeconds { get; set; } = 5;
    public int MaxBackoffSeconds { get; set; } = 900;

    /// <summary>A claimed message that is not finished within this time is picked up again (worker crash safety).</summary>
    public int LeaseSeconds { get; set; } = 120;
    public int BatchSize { get; set; } = 10;

    /// <summary>Managed mode: a message for a session whose engine is not ready (preparing, needs QR, restarting) is re-queued after this delay WITHOUT counting an attempt.</summary>
    public int NotReadyRetrySeconds { get; set; } = 15;
    /// <summary>... but only while the message is younger than this; older ones fail as a normal transient failure so the queue cannot grow forever.</summary>
    public int NotReadyMaxAgeMinutes { get; set; } = 60;

    public TimeSpan BackoffFor(int attemptNumber)
    {
        var seconds = Math.Min((double)MaxBackoffSeconds, BaseBackoffSeconds * Math.Pow(2, Math.Max(0, attemptNumber - 1)));
        return TimeSpan.FromSeconds(seconds);
    }
}

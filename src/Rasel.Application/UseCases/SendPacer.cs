using Rasel.Application.Common;

namespace Rasel.Application.UseCases;

public enum SendPriority { Bulk, Otp }

/// <summary>Spaces out sends on one WhatsApp session so traffic does not look robotic.</summary>
public interface ISendPacer
{
    Task WaitTurnAsync(Guid sessionId, SendPriority priority, CancellationToken ct);
}

/// <summary>
/// Bulk: waits a random jitter and at least <c>MinGapMs</c> after the previous send, and yields while an OTP waits.
/// OTP: never waits for the gap or for bulk; it only gets the random jitter, and only when the session sent
/// something less than <c>MinGapMs</c> ago (an idle session sends an OTP immediately).
/// </summary>
public sealed class SendPacer(PacingOptions options, TimeProvider clock, IRandomSource random) : ISendPacer
{
    private sealed class State { public DateTimeOffset LastSend = DateTimeOffset.MinValue; public int OtpWaiting; }

    private readonly Dictionary<Guid, State> _sessions = [];
    private readonly object _lock = new();

    public async Task WaitTurnAsync(Guid sessionId, SendPriority priority, CancellationToken ct)
    {
        State state;
        lock (_lock) state = _sessions.TryGetValue(sessionId, out var s) ? s : _sessions[sessionId] = new State();

        if (priority == SendPriority.Otp)
        {
            TimeSpan wait;
            lock (_lock)
            {
                state.OtpWaiting++;
                var now = clock.GetUtcNow();
                wait = now - state.LastSend < TimeSpan.FromMilliseconds(options.MinGapMs) ? Jitter() : TimeSpan.Zero;
                state.LastSend = Max(state.LastSend, now + wait);
            }
            try { if (wait > TimeSpan.Zero) await Task.Delay(wait, clock, ct); }
            finally { lock (_lock) state.OtpWaiting--; }
            return;
        }

        while (true)
        {
            TimeSpan wait;
            var reserved = false;
            lock (_lock)
            {
                if (state.OtpWaiting > 0) wait = TimeSpan.FromMilliseconds(50);
                else
                {
                    var now = clock.GetUtcNow();
                    var target = Max(now + Jitter(), state.LastSend + TimeSpan.FromMilliseconds(options.MinGapMs));
                    state.LastSend = target;
                    wait = target - now;
                    reserved = true;
                }
            }
            if (wait > TimeSpan.Zero) await Task.Delay(wait, clock, ct);
            if (reserved) return;
        }
    }

    private TimeSpan Jitter() => TimeSpan.FromMilliseconds(random.Next(options.MinDelayMs, options.MaxDelayMs + 1));

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}

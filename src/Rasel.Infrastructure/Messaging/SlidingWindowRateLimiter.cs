using System.Collections.Concurrent;
using Rasel.Application.Ports;

namespace Rasel.Infrastructure.Messaging;

/// <summary>In-process sliding-window limiter. Idle windows are evicted so random keys cannot grow memory without bound.</summary>
public sealed class SlidingWindowRateLimiter(TimeProvider clock) : IRateLimiter
{
    private const int SweepEvery = 512;
    private readonly ConcurrentDictionary<string, Window> _windows = new();
    private int _calls;

    private sealed class Window(TimeSpan span)
    {
        public readonly Queue<DateTimeOffset> Hits = new();
        public TimeSpan Span { get; set; } = span;
    }

    public int TrackedKeys => _windows.Count;

    public bool TryAcquire(string key, int limit, TimeSpan window)
    {
        var now = clock.GetUtcNow();
        if (Interlocked.Increment(ref _calls) % SweepEvery == 0) Sweep(now);

        var entry = _windows.GetOrAdd(key, _ => new Window(window));
        lock (entry)
        {
            entry.Span = window;
            Trim(entry, now);
            if (entry.Hits.Count >= limit) return false;
            entry.Hits.Enqueue(now);
            return true;
        }
    }

    public bool IsExceeded(string key, int limit, TimeSpan window)
    {
        if (!_windows.TryGetValue(key, out var entry)) return false;
        lock (entry)
        {
            Trim(entry, clock.GetUtcNow());
            return entry.Hits.Count >= limit;
        }
    }

    private static void Trim(Window entry, DateTimeOffset now)
    {
        while (entry.Hits.Count > 0 && now - entry.Hits.Peek() >= entry.Span) entry.Hits.Dequeue();
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (key, entry) in _windows)
        {
            lock (entry)
            {
                Trim(entry, now);
                if (entry.Hits.Count == 0) _windows.TryRemove(new KeyValuePair<string, Window>(key, entry));
            }
        }
    }
}

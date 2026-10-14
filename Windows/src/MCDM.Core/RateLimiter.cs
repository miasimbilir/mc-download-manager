using System.Diagnostics;

namespace MCDM;

/// The speed limit: one budget for every connection of every file download. A connection reports what it
/// has just read and waits for its turn before reading on; the waits line the connections up so that
/// together they stay at the limit.
public static class RateLimiter
{
    private static readonly object gate = new();
    private static readonly Stopwatch clock = Stopwatch.StartNew();
    private static long bytesPerSecond;         // 0 = no limit
    private static double nextFree;             // clock time (seconds) at which the budget is free again
    private static int generation;              // changes with the limit; waits booked under the old one end

    /// A wait that ends late (Windows timers tick every 15 ms) is made up for, up to this many seconds'
    /// worth of the budget; without it a single connection would stay below the limit.
    private const double Slack = 0.1;

    public static long BytesPerSecond
    {
        get => Interlocked.Read(ref bytesPerSecond);
        set
        {
            lock (gate)
            {
                bytesPerSecond = Math.Max(0, value);
                nextFree = 0;                   // a new limit applies from now, not after the old one's queue
                generation++;
            }
        }
    }

    /// How much to read at once: about a tenth of a second's worth while a limit is set, so the progress
    /// moves evenly instead of in bursts.
    public static int ChunkSize(int max)
    {
        var rate = BytesPerSecond;
        return rate <= 0 ? max : (int)Math.Clamp(rate / 10, 1024, max);
    }

    /// Books `bytes` against the budget and returns how long the caller has to wait for its turn.
    public static TimeSpan Reserve(int bytes) => Reserve(bytes, out _);

    private static TimeSpan Reserve(int bytes, out int booked)
    {
        lock (gate)
        {
            booked = generation;
            if (bytesPerSecond <= 0 || bytes <= 0) return TimeSpan.Zero;
            var now = clock.Elapsed.TotalSeconds;
            var start = Math.Max(now - Slack, nextFree);
            nextFree = start + (double)bytes / bytesPerSecond;
            return TimeSpan.FromSeconds(Math.Max(0, nextFree - now));
        }
    }

    /// Waits for the turn of a connection that has just read `bytes`. Returns at once without a limit,
    /// and early when the limit is changed or lifted meanwhile.
    public static async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        var left = Reserve(bytes, out var booked);
        while (left > TimeSpan.FromMilliseconds(2))
        {
            var slice = left < TimeSpan.FromMilliseconds(250) ? left : TimeSpan.FromMilliseconds(250);
            await Task.Delay(slice, ct).ConfigureAwait(false);
            left -= slice;
            if (Volatile.Read(ref generation) != booked) return;
        }
    }

    /// True when the next `WaitAsync` for this many bytes would not return at once.
    public static bool Limited => BytesPerSecond > 0;
}

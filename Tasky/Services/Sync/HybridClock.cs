using System.Globalization;

namespace Tasky.Services.Sync;

/// <summary>
/// Hybrid logical clock: UTC milliseconds, a counter and the device id, written as a fixed-width
/// string ("0001762876543210.0000.devid") so clocks compare with a plain ordinal string compare.
/// It never goes backwards, even if this computer's clock is set back, and after seeing another
/// computer's clock it always stamps later than it, so "newest change wins" stays consistent
/// between computers whose clocks disagree by a few minutes. The device id breaks exact ties,
/// so every computer picks the same winner.
/// </summary>
public sealed class HybridClock
{
    private readonly string _deviceId;
    private readonly Func<long> _nowMs;
    private long _ms;
    private int  _counter;
    private readonly Lock _lock = new();

    public HybridClock(string deviceId, Func<long>? nowMs = null)
    {
        _deviceId = deviceId;
        _nowMs    = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>A new clock value, later than every value made or seen so far.</summary>
    public string Tick()
    {
        lock (_lock)
        {
            long now = _nowMs();
            if (now > _ms) { _ms = now; _counter = 0; }
            else _counter++;
            return Format(_ms, _counter, _deviceId);
        }
    }

    /// <summary>Moves this clock past a value from another computer.</summary>
    public void Observe(string? clock)
    {
        if (clock is null || !TryParse(clock, out long ms, out int counter)) return;
        lock (_lock)
        {
            if (ms > _ms || (ms == _ms && counter > _counter)) { _ms = ms; _counter = counter; }
        }
    }

    public static int Compare(string? a, string? b) => string.CompareOrdinal(a ?? string.Empty, b ?? string.Empty);

    public static string Max(string a, string b) => Compare(a, b) >= 0 ? a : b;

    private static string Format(long ms, int counter, string device)
        => ms.ToString("D16", CultureInfo.InvariantCulture) + "." + counter.ToString("D4", CultureInfo.InvariantCulture) + "." + device;

    private static bool TryParse(string clock, out long ms, out int counter)
    {
        ms = 0; counter = 0;
        var parts = clock.Split('.', 3);
        return parts.Length == 3
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out ms)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out counter);
    }
}

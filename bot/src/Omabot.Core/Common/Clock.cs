// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Omabot.Common;

/// <summary>Time source. Every time-based rule takes a clock so tests can move time.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>A clock tests can set and advance.</summary>
public sealed class ManualClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _now = start;
    private readonly Lock _lock = new();

    public DateTimeOffset UtcNow { get { lock (_lock) return _now; } }

    public void Advance(TimeSpan by) { lock (_lock) _now = _now.Add(by); }

    public void Set(DateTimeOffset to) { lock (_lock) _now = to; }
}

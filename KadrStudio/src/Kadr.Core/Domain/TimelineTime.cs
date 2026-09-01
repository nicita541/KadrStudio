using System.Globalization;
using System.Text.Json.Serialization;

namespace KadrStudio.Core.Domain;

/// <summary>
/// Exact timeline time. The 240 kHz timebase represents common broadcast frame
/// rates (23.976, 24, 25, 29.97, 30, 50, 59.94 and 60) without accumulating
/// floating-point drift.
/// </summary>
public readonly record struct TimelineTime(long Ticks) : IComparable<TimelineTime>
{
    public const long TicksPerSecond = 240_000;

    public static TimelineTime Zero => new(0);
    public double TotalSeconds => Ticks / (double)TicksPerSecond;

    public static TimelineTime FromSeconds(double seconds)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        return new TimelineTime(checked((long)Math.Round(
            seconds * TicksPerSecond,
            MidpointRounding.AwayFromZero)));
    }

    public static TimelineTime FromFrames(long frame, FrameRate frameRate)
        => new(checked((long)Math.Round(
            frame * (decimal)TicksPerSecond * frameRate.Denominator / frameRate.Numerator,
            MidpointRounding.AwayFromZero)));

    public long ToNearestFrame(FrameRate frameRate)
        => checked((long)Math.Round(
            Ticks * (decimal)frameRate.Numerator / (TicksPerSecond * (decimal)frameRate.Denominator),
            MidpointRounding.AwayFromZero));

    public TimelineTime SnapToFrame(FrameRate frameRate)
        => FromFrames(ToNearestFrame(frameRate), frameRate);

    public int CompareTo(TimelineTime other) => Ticks.CompareTo(other.Ticks);

    public override string ToString()
        => TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);

    public static TimelineTime operator +(TimelineTime left, TimelineTime right)
        => new(checked(left.Ticks + right.Ticks));

    public static TimelineTime operator -(TimelineTime left, TimelineTime right)
        => new(checked(left.Ticks - right.Ticks));

    public static TimelineTime operator -(TimelineTime value) => new(checked(-value.Ticks));
    public static bool operator <(TimelineTime left, TimelineTime right) => left.Ticks < right.Ticks;
    public static bool operator >(TimelineTime left, TimelineTime right) => left.Ticks > right.Ticks;
    public static bool operator <=(TimelineTime left, TimelineTime right) => left.Ticks <= right.Ticks;
    public static bool operator >=(TimelineTime left, TimelineTime right) => left.Ticks >= right.Ticks;
}

public readonly record struct FrameRate
{
    [JsonConstructor]
    public FrameRate(int numerator, int denominator = 1)
    {
        if (numerator <= 0) throw new ArgumentOutOfRangeException(nameof(numerator));
        if (denominator <= 0) throw new ArgumentOutOfRangeException(nameof(denominator));
        var divisor = GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / divisor;
        Denominator = denominator / divisor;
    }

    public int Numerator { get; }
    public int Denominator { get; }
    public double FramesPerSecond => Numerator / (double)Denominator;
    public TimelineTime FrameDuration => TimelineTime.FromFrames(1, this);

    public static FrameRate Fps23976 => new(24_000, 1_001);
    public static FrameRate Fps24 => new(24);
    public static FrameRate Fps25 => new(25);
    public static FrameRate Fps2997 => new(30_000, 1_001);
    public static FrameRate Fps30 => new(30);
    public static FrameRate Fps50 => new(50);
    public static FrameRate Fps5994 => new(60_000, 1_001);
    public static FrameRate Fps60 => new(60);

    public override string ToString()
        => Denominator == 1 ? Numerator.ToString(CultureInfo.InvariantCulture) : $"{Numerator}/{Denominator}";

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }
        return Math.Abs(left);
    }
}

public readonly record struct TimeRange
{
    [JsonConstructor]
    public TimeRange(TimelineTime start, TimelineTime duration)
    {
        if (start < TimelineTime.Zero) throw new ArgumentOutOfRangeException(nameof(start));
        if (duration <= TimelineTime.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        Start = start;
        Duration = duration;
    }

    public TimelineTime Start { get; }
    public TimelineTime Duration { get; }
    public TimelineTime End => Start + Duration;
    public bool Contains(TimelineTime time) => time >= Start && time < End;
    public bool Overlaps(TimeRange other) => Start < other.End && End > other.Start;
}

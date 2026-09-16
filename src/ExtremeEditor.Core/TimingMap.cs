using System.Numerics;

namespace ExtremeEditor.Core;

public readonly record struct FloorTiming(
    int Floor,
    double EntryTime,
    double ExitTime,
    double EntryAngle,
    double ExitAngle,
    double AngleMoved,
    double Bpm,
    bool IsCcw,
    bool MidSpin);

public readonly record struct PlaybackPose(
    int Floor,
    double Progress,
    Vector2 StationaryPlanet,
    Vector2 OrbitingPlanet,
    bool StationaryIsRed,
    bool IsPreStart);

public static class PlaybackClock
{
    public static double AudioToChartTime(LevelDocument level, double audioSeconds)
    {
        double pitch = Math.Max(0.000001, level.PitchPercent * 0.01);
        return audioSeconds * pitch + GetSeparateCountdownChartSeconds(level)
               - level.OffsetMilliseconds * 0.001;
    }

    public static double ChartToAudioTime(LevelDocument level, double chartSeconds)
    {
        double pitch = Math.Max(0.000001, level.PitchPercent * 0.01);
        return (chartSeconds + level.OffsetMilliseconds * 0.001
                - GetSeparateCountdownChartSeconds(level)) / pitch;
    }

    private static double GetSeparateCountdownChartSeconds(LevelDocument level)
    {
        if (!level.SeparateCountdownTime || level.CountdownTicks <= 0)
            return 0.0;

        double bpm = level.InitialBpm > 0 ? level.InitialBpm : 100.0;
        return level.CountdownTicks * (60.0 / bpm);
    }
}

public sealed class TimingMap
{
    private readonly FloorTiming[] _floors;
    private readonly double[] _entryTimes;

    public TimingMap(FloorTiming[] floors)
    {
        _floors = floors;
        _entryTimes = floors.Select(floor => floor.EntryTime).ToArray();
    }

    public IReadOnlyList<FloorTiming> Floors => _floors;
    public double Duration => _floors.Length == 0 ? 0 : _floors[^1].ExitTime;

    public double GetEntryTime(int floor)
    {
        if (_floors.Length == 0) return 0;
        return _floors[Math.Clamp(floor, 0, _floors.Length - 1)].EntryTime;
    }

    public PlaybackPose GetPose(LevelDocument level, double chartTime)
    {
        if (_floors.Length == 0 || level.Positions.Length == 0)
            return new PlaybackPose(0, 0, Vector2.Zero, Vector2.Zero, true, chartTime < 0);

        // Offset controls when chart time zero is reached. Do not stretch the
        // first rotation across the offset; before zero the planets simply hold
        // the exact pose they will have when playback begins.
        if (chartTime < 0)
            return GetPoseCore(level, 0, isPreStart: true);

        return GetPoseCore(level, chartTime, isPreStart: false);
    }

    private PlaybackPose GetPoseCore(LevelDocument level, double chartTime, bool isPreStart)
    {
        int floor = FindFloor(chartTime);
        FloorTiming timing = _floors[floor];
        double duration = timing.ExitTime - timing.EntryTime;
        double progress = duration <= 1e-9
            ? 1.0
            : Math.Clamp((chartTime - timing.EntryTime) / duration, 0.0, 1.0);

        Vector2 stationary = level.Positions[Math.Min(floor, level.Positions.Length - 1)];
        double direction = timing.IsCcw ? -1.0 : 1.0;
        double angle = timing.EntryAngle + direction * timing.AngleMoved * progress;
        Vector2 orbiting = stationary + new Vector2(
            (float)Math.Sin(angle) * PathBuilder.DefaultLongTileSize,
            (float)Math.Cos(angle) * PathBuilder.DefaultLongTileSize);

        bool stationaryIsRed = (floor & 1) == 0;
        return new PlaybackPose(
            floor,
            progress,
            stationary,
            orbiting,
            stationaryIsRed,
            isPreStart);
    }

    private int FindFloor(double chartTime)
    {
        int index = Array.BinarySearch(_entryTimes, chartTime);
        if (index < 0) index = ~index - 1;
        return Math.Clamp(index, 0, _floors.Length - 1);
    }

    internal static double Mod(double value, double modulus)
    {
        double result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}

public static class TimingMapBuilder
{
    private const double TwoPi = Math.PI * 2.0;
    private const double PiStock = 3.1415927410125732;
    private const double InitialEntryAngle = 4.71238898038469;

    public static TimingMap Build(LevelDocument level)
    {
        int floorCount = level.FloorCount;
        if (floorCount == 0)
            return new TimingMap([]);

        var entryAngles = new double[floorCount];
        var exitAngles = new double[floorCount];
        entryAngles[0] = InitialEntryAngle;

        for (int floor = 0; floor < floorCount - 1; floor++)
        {
            double raw = level.Angles[floor];
            double exit = Math.Abs(raw - 999.0) < 0.000001
                ? entryAngles[floor]
                : (-raw + 90.0) * Math.PI / 180.0;
            exitAngles[floor] = exit;
            entryAngles[floor + 1] = TimingMap.Mod(exit + Math.PI, TwoPi);
        }
        exitAngles[^1] = entryAngles[^1] + PiStock;

        var timings = new FloorTiming[floorCount];
        double bpm = level.InitialBpm > 0 ? level.InitialBpm : 100.0;
        bool isCcw = false;
        double time = 0.0;

        for (int floor = 0; floor < floorCount; floor++)
        {
            if (level.ActionsByFloor.TryGetValue(floor, out LevelAction[]? actions))
            {
                foreach (LevelAction action in actions)
                {
                    if (!action.Active) continue;

                    if (string.Equals(action.EventType, "Twirl", StringComparison.Ordinal))
                    {
                        isCcw = !isCcw;
                    }
                    else if (string.Equals(action.EventType, "SetSpeed", StringComparison.Ordinal))
                    {
                        if (string.Equals(action.SpeedType, "Multiplier", StringComparison.OrdinalIgnoreCase) &&
                            action.BpmMultiplier is double multiplier && multiplier > 0)
                        {
                            bpm *= multiplier;
                        }
                        else if (action.BeatsPerMinute is double target && target > 0)
                        {
                            bpm = target;
                        }
                    }
                }
            }

            bool midSpin = floor < level.Angles.Length &&
                           Math.Abs(level.Angles[floor] - 999.0) < 0.000001;
            double moved = GetAngleMoved(entryAngles[floor], exitAngles[floor], !isCcw);
            if (moved <= 1e-6 || moved >= 6.283184482025146)
                moved = midSpin ? 0.0 : TwoPi;

            double visualEntryAngle = entryAngles[floor];
            if (floor == 0)
            {
                // Stock does both of these together:
                //   floor0.angleLength += (adjustedCountdownTicks - 1) * PI
                //   snappedLastAngle = PI * (0.5 - adjustedCountdownTicks)
                // The old prototype added the countdown to time but not to the
                // angle, which made the first orbit comically slow.
                double countdownExtra = Math.Max(0, level.CountdownTicks - 1) * PiStock;
                moved += countdownExtra;
                visualEntryAngle = PiStock * (0.5 - level.CountdownTicks);
            }

            // scrMisc.GetTimeBetweenAngles: angle / PI * crotchet. Offset remains
            // a separate wall-clock transform in PlaybackClock and never changes
            // this angular speed.
            double seconds = moved / PiStock * (60.0 / bpm);
            double exitTime = time + seconds;
            timings[floor] = new FloorTiming(
                floor,
                time,
                exitTime,
                visualEntryAngle,
                exitAngles[floor],
                moved,
                bpm,
                isCcw,
                midSpin);
            time = exitTime;
        }

        return new TimingMap(timings);
    }

    private static double GetAngleMoved(double entry, double exit, bool isCw)
    {
        double sign = isCw ? 1.0 : -1.0;
        return TimingMap.Mod((exit - entry) * sign, TwoPi);
    }
}

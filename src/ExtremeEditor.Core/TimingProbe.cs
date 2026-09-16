namespace ExtremeEditor.Core;

public sealed record TimingProbeRow(
    int Floor,
    double CurrentEntrySeconds,
    double ReferenceEntrySeconds,
    double CurrentFloorSeconds,
    double ReferenceFloorSeconds,
    double CurrentAngleDegrees,
    double ReferenceAngleDegrees,
    double CurrentBpm,
    double ReferenceStartBpm,
    double ReferenceEndBpm,
    bool IsCcw,
    int NumPlanets,
    string SetSpeedOffsets);

public sealed record TimingProbeResult(
    int? FirstMismatchFloor,
    double FirstMismatchCurrentSeconds,
    double FirstMismatchReferenceSeconds,
    double CurrentDurationSeconds,
    double ReferenceDurationSeconds,
    double MaxCumulativeDeltaSeconds,
    int MismatchCount,
    IReadOnlyList<TimingProbeRow> Rows);

/// <summary>
/// Diagnostic-only timing calculator. It intentionally does not feed playback:
/// it independently models the game's core Twirl/MultiPlanet/SetSpeed timing so
/// a divergence can be located without changing TimingMap behavior.
/// </summary>
public static class TimingProbe
{
    private const double Pi = 3.1415927410125732;
    private const double TwoPi = 6.2831854820251465;
    private const double FirstEntryAngle = 4.71238899230957;
    private const double RadiansToDegrees = 57.29578;

    public static TimingProbeResult Analyze(
        LevelDocument level,
        TimingMap timing,
        double toleranceSeconds = 1e-7,
        int contextRadius = 3)
    {
        int floorCount = Math.Min(level.FloorCount, timing.Floors.Count);
        if (floorCount == 0)
        {
            return new TimingProbeResult(
                null, 0, 0, timing.Duration, 0, 0, 0, []);
        }

        double baseBpm = level.InitialBpm > 0 ? level.InitialBpm : 100.0;
        double speedMult = 1.0;
        bool isCcw = false;
        int numPlanets = 2;
        bool previousMidSpin = false;
        double entryAngle = FirstEntryAngle;
        double referenceTime = 0.0;
        double maxCumulativeDelta = 0.0;
        int mismatchCount = 0;
        int? firstMismatchFloor = null;
        double firstMismatchCurrentSeconds = 0.0;
        double firstMismatchReferenceSeconds = 0.0;

        var rows = new List<TimingProbeRow>(Math.Max(1, contextRadius * 2 + 1));
        var recent = new Queue<TimingProbeRow>(Math.Max(1, contextRadius));

        for (int floor = 0; floor < floorCount; floor++)
        {
            bool midSpin = floor < level.Angles.Length &&
                           Math.Abs(level.Angles[floor] - 999.0) < 0.000001;
            double exitAngle = floor < level.Angles.Length
                ? (midSpin ? entryAngle : (90.0 - level.Angles[floor]) * Pi / 180.0)
                : entryAngle + Pi;

            LevelAction[] actions = level.ActionsByFloor.TryGetValue(floor, out LevelAction[]? floorActions)
                ? floorActions.Where(action => action.Active).ToArray()
                : [];

            foreach (LevelAction action in actions)
            {
                if (string.Equals(action.EventType, "Twirl", StringComparison.Ordinal))
                {
                    isCcw = !isCcw;
                }
                else if (string.Equals(action.EventType, "MultiPlanet", StringComparison.Ordinal))
                {
                    if (string.Equals(action.Planets, "ThreePlanets", StringComparison.OrdinalIgnoreCase))
                        numPlanets = 3;
                    else if (string.Equals(action.Planets, "TwoPlanets", StringComparison.OrdinalIgnoreCase))
                        numPlanets = 2;
                }
            }

            double rawMoved = GetAngleMoved(entryAngle, exitAngle, !isCcw);
            if (rawMoved <= 1e-6 || rawMoved >= 6.283184482025146)
                rawMoved = midSpin ? 0.0 : TwoPi;

            double referenceMoved = GetReferenceAngleMoved(
                entryAngle,
                exitAngle,
                isCcw,
                midSpin,
                numPlanets,
                previousMidSpin);

            double startBpm = baseBpm * speedMult;
            LevelAction[] speeds = actions
                .Where(action => string.Equals(action.EventType, "SetSpeed", StringComparison.Ordinal))
                .ToArray();
            double speedAverage = ApplySpeedEvents(
                speeds,
                rawMoved * RadiansToDegrees,
                baseBpm,
                ref speedMult,
                out string speedOffsets);
            double endBpm = baseBpm * speedMult;

            double referenceFloorSeconds = referenceMoved / Pi * (60.0 / baseBpm / speedAverage);
            FloorTiming current = timing.Floors[floor];
            double currentFloorSeconds = current.ExitTime - current.EntryTime;

            // The prototype currently folds countdown visualization into floor 0,
            // while the stock entry-time calculation treats countdown separately.
            // Anchor the probe after floor 0 so that known clock-policy difference
            // does not hide later core-event timing divergence.
            if (floor == 0)
                referenceFloorSeconds = currentFloorSeconds;

            var row = new TimingProbeRow(
                floor,
                current.EntryTime,
                referenceTime,
                currentFloorSeconds,
                referenceFloorSeconds,
                current.AngleMoved * 180.0 / Pi,
                referenceMoved * 180.0 / Pi,
                current.Bpm,
                startBpm,
                endBpm,
                isCcw,
                numPlanets,
                speedOffsets);

            bool mismatch = floor > 0 &&
                            (Math.Abs(currentFloorSeconds - referenceFloorSeconds) > toleranceSeconds ||
                             Math.Abs(current.AngleMoved - referenceMoved) > 1e-7);
            if (mismatch)
            {
                mismatchCount++;
                if (firstMismatchFloor is null)
                {
                    firstMismatchFloor = floor;
                    firstMismatchCurrentSeconds = currentFloorSeconds;
                    firstMismatchReferenceSeconds = referenceFloorSeconds;
                    rows.AddRange(recent);
                    rows.Add(row);
                }
            }
            else if (firstMismatchFloor is null && contextRadius > 0)
            {
                recent.Enqueue(row);
                while (recent.Count > contextRadius)
                    recent.Dequeue();
            }
            else if (firstMismatchFloor is int first && floor <= first + contextRadius)
            {
                rows.Add(row);
            }

            referenceTime += referenceFloorSeconds;
            maxCumulativeDelta = Math.Max(
                maxCumulativeDelta,
                Math.Abs(current.ExitTime - referenceTime));

            previousMidSpin = midSpin;
            entryAngle = Mod(exitAngle + Pi, TwoPi);
        }

        return new TimingProbeResult(
            firstMismatchFloor,
            firstMismatchCurrentSeconds,
            firstMismatchReferenceSeconds,
            timing.Duration,
            referenceTime,
            maxCumulativeDelta,
            mismatchCount,
            rows);
    }

    private static double GetReferenceAngleMoved(
        double entryAngle,
        double exitAngle,
        bool isCcw,
        bool midSpin,
        int numPlanets,
        bool previousMidSpin)
    {
        double inverse = Pi * (numPlanets - 2.0) / numPlanets;
        double offset = inverse * (isCcw ? -1.0 : 1.0);
        if (midSpin)
            offset = 0.0;

        if (previousMidSpin && numPlanets > 2)
            offset -= (TwoPi + inverse) * (isCcw ? -1.0 : 1.0);

        double moved = GetAngleMoved(
            entryAngle + offset,
            exitAngle + (midSpin ? offset : 0.0),
            !isCcw);
        if (moved <= 1e-6 || moved >= 6.283184482025146)
            moved = midSpin ? 0.0 : TwoPi;
        return moved;
    }

    private static double ApplySpeedEvents(
        LevelAction[] speeds,
        double floorAngleDegrees,
        double baseBpm,
        ref double speedMult,
        out string offsetsText)
    {
        if (speeds.Length == 0)
        {
            offsetsText = "-";
            return speedMult;
        }

        var groups = speeds
            .GroupBy(speed => speed.AngleOffset ?? 0.0)
            .OrderBy(group => group.Key)
            .ToArray();
        offsetsText = string.Join(",", groups.Select(group => group.Key.ToString("0.###")));

        double activeBpm = baseBpm * speedMult;
        double cursorDegrees = 0.0;
        double totalSeconds = 0.0;
        bool hasMidFloorOffset = false;

        foreach (IGrouping<double, LevelAction> group in groups)
        {
            double offset = group.Key;
            double clampedOffset = Math.Clamp(offset, 0.0, Math.Max(0.0, floorAngleDegrees));
            if (clampedOffset > cursorDegrees)
            {
                totalSeconds += (clampedOffset - cursorDegrees) / 180.0 * (60.0 / activeBpm);
                cursorDegrees = clampedOffset;
            }

            foreach (LevelAction speed in group)
                activeBpm = ApplySpeed(activeBpm, speed);

            if (offset > 0.0 && offset <= floorAngleDegrees)
                hasMidFloorOffset = true;
        }

        if (cursorDegrees < floorAngleDegrees)
            totalSeconds += (floorAngleDegrees - cursorDegrees) / 180.0 * (60.0 / activeBpm);

        speedMult = activeBpm / baseBpm;
        if (!hasMidFloorOffset || floorAngleDegrees <= 1e-12 || totalSeconds <= 1e-12)
            return speedMult;

        return 60.0 / totalSeconds * (floorAngleDegrees / 180.0) / baseBpm;
    }

    private static double ApplySpeed(double currentBpm, LevelAction speed)
    {
        if (string.Equals(speed.SpeedType, "Multiplier", StringComparison.OrdinalIgnoreCase) &&
            speed.BpmMultiplier is double multiplier && multiplier > 0.0)
        {
            return currentBpm * multiplier;
        }

        if (speed.BeatsPerMinute is double target && target > 0.0)
            return target;

        return currentBpm;
    }

    private static double GetAngleMoved(double entryAngle, double exitAngle, bool isCw)
    {
        double sign = isCw ? 1.0 : -1.0;
        return Mod((exitAngle - entryAngle) * sign, TwoPi);
    }

    private static double Mod(double value, double modulus)
    {
        double result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}

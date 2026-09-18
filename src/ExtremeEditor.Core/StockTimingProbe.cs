namespace ExtremeEditor.Core;

public sealed record StockTimingProbeRow(
    int Floor,
    double CurrentEntrySeconds,
    double StockEntrySeconds,
    double CurrentFloorSeconds,
    double StockFloorSeconds,
    double CurrentAngleDegrees,
    double StockAngleDegrees,
    double CurrentBpm,
    double StockBpm,
    bool IsCcw,
    int NumPlanets,
    string SetSpeedOffsets);

public sealed record StockTimingProbeResult(
    int? FirstMismatchFloor,
    double FirstMismatchCurrentSeconds,
    double FirstMismatchStockSeconds,
    double CurrentDurationSeconds,
    double StockDurationSeconds,
    double MaxCumulativeDeltaSeconds,
    int MismatchCount,
    IReadOnlyList<StockTimingProbeRow> Rows);

/// <summary>
/// Diagnostic-only reproduction of the stock timing path relevant to the
/// prototype's parsed timing events:
/// InstantiateFloatFloors -> ApplyCoreEventsToFloors -> CalculateFloorEntryTimes.
/// It deliberately does not feed playback.
/// </summary>
public static class StockTimingProbe
{
    private const double PiStock = 3.1415927410125732;
    private const double TwoPi = 6.2831854820251465;
    private const double FirstEntryAngle = 4.71238899230957;
    private const float DegreesToRadiansStock = 0.017453292f;
    private const float RadiansToDegreesStock = 57.29578f;
    private static readonly LevelAction[] EmptyActions = [];

    public static StockTimingProbeResult Analyze(
        LevelDocument level,
        TimingMap timing,
        double toleranceSeconds = 1e-7,
        int contextRadius = 3)
    {
        int floorCount = Math.Min(level.FloorCount, timing.Floors.Count);
        if (floorCount == 0)
        {
            return new StockTimingProbeResult(
                null, 0, 0, timing.Duration, 0, 0, 0, []);
        }

        float baseBpm = level.InitialBpm > 0 ? (float)level.InitialBpm : 100f;
        float speedMult = 1f;
        bool isCcw = false;
        int numPlanets = 2;
        double stockTime = 0.0;
        double maxCumulativeDelta = 0.0;
        int mismatchCount = 0;
        int? firstMismatchFloor = null;
        double firstMismatchCurrentSeconds = 0.0;
        double firstMismatchStockSeconds = 0.0;

        var rows = new List<StockTimingProbeRow>(Math.Max(1, contextRadius * 2 + 1));
        var recent = new Queue<StockTimingProbeRow>(Math.Max(1, contextRadius));

        StockFloorState? pending = null;
        bool previousMidSpin = false;
        double entryAngle = FirstEntryAngle;

        for (int floor = 0; floor < floorCount; floor++)
        {
            StockFloorState current = BuildGeometry(level, floor, floorCount, entryAngle, previousMidSpin);
            LevelAction[] actions = level.ActionsByFloor.TryGetValue(floor, out LevelAction[]? floorActions)
                ? floorActions
                : EmptyActions;

            ApplyCoreEvents(
                actions,
                ref current,
                ref pending,
                baseBpm,
                ref speedMult,
                ref isCcw,
                ref numPlanets);

            if (pending is StockFloorState ready)
            {
                double stockFloorSeconds = GetFloorSeconds(
                    ready,
                    baseBpm,
                    ready.Floor == 0 ? level.CountdownTicks : 0);
                CompareFloor(
                    ready,
                    stockTime,
                    stockFloorSeconds,
                    timing,
                    toleranceSeconds,
                    contextRadius,
                    rows,
                    recent,
                    ref firstMismatchFloor,
                    ref firstMismatchCurrentSeconds,
                    ref firstMismatchStockSeconds,
                    ref mismatchCount);

                stockTime += stockFloorSeconds;
                FloorTiming currentTiming = timing.Floors[ready.Floor];
                maxCumulativeDelta = Math.Max(
                    maxCumulativeDelta,
                    Math.Abs(currentTiming.ExitTime - stockTime));
            }

            pending = current;
            previousMidSpin = current.MidSpin;
            entryAngle = current.NextEntryAngle;
        }

        if (pending is StockFloorState last)
        {
            double stockFloorSeconds = GetFloorSeconds(last, baseBpm, 0);
            CompareFloor(
                last,
                stockTime,
                stockFloorSeconds,
                timing,
                toleranceSeconds,
                contextRadius,
                rows,
                recent,
                ref firstMismatchFloor,
                ref firstMismatchCurrentSeconds,
                ref firstMismatchStockSeconds,
                ref mismatchCount);

            stockTime += stockFloorSeconds;
            FloorTiming currentTiming = timing.Floors[last.Floor];
            maxCumulativeDelta = Math.Max(
                maxCumulativeDelta,
                Math.Abs(currentTiming.ExitTime - stockTime));
        }

        return new StockTimingProbeResult(
            firstMismatchFloor,
            firstMismatchCurrentSeconds,
            firstMismatchStockSeconds,
            timing.Duration,
            stockTime,
            maxCumulativeDelta,
            mismatchCount,
            rows);
    }

    private static StockFloorState BuildGeometry(
        LevelDocument level,
        int floor,
        int floorCount,
        double entryAngle,
        bool previousMidSpin)
    {
        bool hasAngle = floor < level.Angles.Length;
        float raw = hasAngle ? (float)level.Angles[floor] : 0f;
        bool midSpin = hasAngle && raw == 999f;

        double exitAngle;
        if (!hasAngle)
        {
            exitAngle = entryAngle + PiStock;
        }
        else if (midSpin)
        {
            // InstantiateFloatFloors explicitly narrows the entry angle to float
            // for midspins before storing it back in the double field.
            exitAngle = (double)(float)entryAngle;
        }
        else
        {
            exitAngle = (-(double)raw + 90f) * DegreesToRadiansStock;
        }

        double nextEntryAngle = floor + 1 < floorCount
            ? (exitAngle + PiStock) % TwoPi
            : entryAngle;

        return new StockFloorState
        {
            Floor = floor,
            EntryAngle = entryAngle,
            ExitAngle = exitAngle,
            NextEntryAngle = nextEntryAngle,
            Speed = 1f,
            IsCcw = false,
            NumPlanets = 2,
            MidSpin = midSpin,
            PreviousMidSpin = previousMidSpin,
            SetSpeedOffsets = "-"
        };
    }

    private static void ApplyCoreEvents(
        LevelAction[] actions,
        ref StockFloorState current,
        ref StockFloorState? previous,
        float baseBpm,
        ref float speedMult,
        ref bool isCcw,
        ref int numPlanets)
    {
        int setSpeedCount = 0;
        foreach (LevelAction action in actions)
        {
            if (!action.Active)
                continue;

            if (string.Equals(action.EventType, "SetSpeed", StringComparison.Ordinal))
                setSpeedCount++;

            if (string.Equals(action.EventType, "Twirl", StringComparison.Ordinal))
            {
                isCcw = !isCcw;
            }
            else if (string.Equals(action.EventType, "MultiPlanet", StringComparison.Ordinal))
            {
                int nextPlanets = ParsePlanets(action.Planets, numPlanets);
                numPlanets = Math.Clamp(nextPlanets, 2, 3);

                // Stock also writes the new planet count onto the previous floor
                // when the event follows a midspin.
                if (previous is StockFloorState prior && prior.MidSpin)
                {
                    prior.NumPlanets = numPlanets;
                    previous = prior;
                }
            }
        }

        float speedAverage = speedMult;
        bool hasMidFloorOffset = false;
        if (setSpeedCount > 0)
        {
            speedAverage = ApplySetSpeeds(
                actions,
                current.EntryAngle,
                current.ExitAngle,
                isCcw,
                baseBpm,
                ref speedMult,
                out hasMidFloorOffset,
                out string offsetsText);
            current.SetSpeedOffsets = offsetsText;
        }

        current.Speed = hasMidFloorOffset ? speedAverage : speedMult;
        current.IsCcw = isCcw;
        current.NumPlanets = numPlanets;
    }

    private static float ApplySetSpeeds(
        LevelAction[] actions,
        double entryAngle,
        double exitAngle,
        bool isCcw,
        float baseBpm,
        ref float speedMult,
        out bool hasMidFloorOffset,
        out string offsetsText)
    {
        var speeds = new List<LevelAction>();
        foreach (LevelAction action in actions)
        {
            if (action.Active && string.Equals(action.EventType, "SetSpeed", StringComparison.Ordinal))
                speeds.Add(action);
        }

        var offsets = new List<float>();
        foreach (LevelAction speed in speeds)
        {
            float offset = (float)(speed.AngleOffset ?? 0.0);
            if (!offsets.Contains(offset))
                offsets.Add(offset);
        }
        offsets.Sort();

        offsetsText = offsets.Count == 0
            ? "-"
            : string.Join(",", offsets.Select(offset => offset.ToString("0.###")));
        hasMidFloorOffset = false;
        if (offsets.Count == 0)
            return speedMult;

        float floorAngleDegrees = ApproximatelyFloor(entryAngle, exitAngle)
            ? 360f
            : (float)GetAngleMoved(entryAngle, exitAngle, !isCcw) * RadiansToDegreesStock;

        float initialBpm = baseBpm * speedMult;
        float initialCrotchet = 60f / initialBpm;
        float totalTime = offsets[0] / 180f * initialCrotchet;

        foreach (float angleOffset in offsets)
        {
            LevelAction? lastAtOffset = null;
            for (int i = speeds.Count - 1; i >= 0; i--)
            {
                if ((float)(speeds[i].AngleOffset ?? 0.0) == angleOffset)
                {
                    lastAtOffset = speeds[i];
                    break;
                }
            }

            if (lastAtOffset is null)
                continue;

            float nextOffset = 0f;
            foreach (float candidate in offsets)
            {
                if (candidate > angleOffset)
                {
                    nextOffset = candidate;
                    break;
                }
            }
            if (nextOffset == 0f)
                nextOffset = floorAngleDegrees;

            float newBpm;
            if (string.Equals(lastAtOffset.SpeedType, "Bpm", StringComparison.OrdinalIgnoreCase))
            {
                newBpm = (float)(lastAtOffset.BeatsPerMinute ?? (baseBpm * speedMult));
            }
            else
            {
                newBpm = 0f;
                int sameOffsetIndex = 0;
                foreach (LevelAction speed in speeds)
                {
                    if ((float)(speed.AngleOffset ?? 0.0) != angleOffset)
                        continue;

                    if (string.Equals(speed.SpeedType, "Bpm", StringComparison.OrdinalIgnoreCase))
                    {
                        newBpm = (float)(speed.BeatsPerMinute ?? (baseBpm * speedMult));
                    }
                    else
                    {
                        float multiplier = (float)(speed.BpmMultiplier ?? 1.0);
                        if (sameOffsetIndex > 0)
                            newBpm *= multiplier;
                        else
                            newBpm = baseBpm * speedMult * multiplier;
                    }
                    sameOffsetIndex++;
                }
            }

            if (!(newBpm > 0f))
                newBpm = baseBpm * speedMult;

            float segmentCrotchet = 60f / newBpm;
            bool inFloor = angleOffset > 0f && angleOffset <= floorAngleDegrees;
            totalTime += (nextOffset - angleOffset) / 180f * segmentCrotchet;
            if (inFloor)
                hasMidFloorOffset = true;

            speedMult = newBpm / baseBpm;
        }

        if (!hasMidFloorOffset || !(totalTime > 0f))
            return speedMult;

        return 60f / totalTime * (floorAngleDegrees / 180f) / baseBpm;
    }

    private static double GetFloorSeconds(
        StockFloorState floor,
        float baseBpm,
        int countdownTicks)
    {
        if (floor.Floor == 0)
        {
            // scrConductor.crotchetAtStart is initialized through float division.
            double crotchetAtStart = (double)(60f / baseBpm);
            float countdownExtra = countdownTicks - 1f;
            return crotchetAtStart * countdownExtra +
                   GetTimeBetweenAngles(
                       floor.EntryAngle,
                       floor.ExitAngle,
                       floor.Speed,
                       baseBpm,
                       !floor.IsCcw);
        }

        double inverse = GetInverseAnglePerBeatMultiplanet(floor.NumPlanets);
        double offset = inverse * (floor.IsCcw ? -1.0 : 1.0);
        if (floor.MidSpin)
            offset = 0.0;
        if (floor.PreviousMidSpin && floor.NumPlanets > 2)
        {
            offset -= (TwoPi + inverse) * (floor.IsCcw ? -1.0 : 1.0);
        }

        double seconds = GetTimeBetweenAngles(
            floor.EntryAngle + offset,
            floor.ExitAngle + (floor.MidSpin ? offset : 0.0),
            floor.Speed,
            baseBpm,
            !floor.IsCcw);

        double crotchetAtStart = (double)(60f / baseBpm);
        bool turnaround = seconds <= 1e-6 ||
                          seconds >= (double)(2f * (float)crotchetAtStart / floor.Speed) - 1e-6;
        if (turnaround)
        {
            seconds = floor.MidSpin
                ? 0.0
                : 2.0 * GetTimeBetweenAngles(0.0, PiStock, floor.Speed, baseBpm, false);
        }

        return seconds;
    }

    private static double GetStockAngleMoved(StockFloorState floor)
    {
        if (floor.Floor == 0)
            return GetAngleMoved(floor.EntryAngle, floor.ExitAngle, !floor.IsCcw);

        double inverse = GetInverseAnglePerBeatMultiplanet(floor.NumPlanets);
        double offset = inverse * (floor.IsCcw ? -1.0 : 1.0);
        if (floor.MidSpin)
            offset = 0.0;
        if (floor.PreviousMidSpin && floor.NumPlanets > 2)
            offset -= (TwoPi + inverse) * (floor.IsCcw ? -1.0 : 1.0);

        double moved = GetAngleMoved(
            floor.EntryAngle + offset,
            floor.ExitAngle + (floor.MidSpin ? offset : 0.0),
            !floor.IsCcw);
        if (Math.Abs(moved) <= 1e-6 || Math.Abs(moved) >= 6.283184482025146)
            moved = floor.MidSpin ? 0.0 : (double)6.2831855f;
        return moved;
    }

    private static void CompareFloor(
        StockFloorState stock,
        double stockEntrySeconds,
        double stockFloorSeconds,
        TimingMap timing,
        double toleranceSeconds,
        int contextRadius,
        List<StockTimingProbeRow> rows,
        Queue<StockTimingProbeRow> recent,
        ref int? firstMismatchFloor,
        ref double firstMismatchCurrentSeconds,
        ref double firstMismatchStockSeconds,
        ref int mismatchCount)
    {
        FloorTiming current = timing.Floors[stock.Floor];
        double currentFloorSeconds = current.ExitTime - current.EntryTime;
        double stockAngle = GetStockAngleMoved(stock);
        var row = new StockTimingProbeRow(
            stock.Floor,
            current.EntryTime,
            stockEntrySeconds,
            currentFloorSeconds,
            stockFloorSeconds,
            current.AngleMoved * 180.0 / Math.PI,
            stockAngle * 180.0 / PiStock,
            current.Bpm,
            stock.Speed * (double)(float)Math.Max(0.000001, timing.Floors[0].Bpm),
            stock.IsCcw,
            stock.NumPlanets,
            stock.SetSpeedOffsets);

        // Floor 0 is countdown policy rather than core-event timing. Keep it in
        // cumulative totals, but locate divergences from floor 1 onward.
        bool mismatch = stock.Floor > 0 &&
                        Math.Abs(currentFloorSeconds - stockFloorSeconds) > toleranceSeconds;
        if (mismatch)
        {
            mismatchCount++;
            if (firstMismatchFloor is null)
            {
                firstMismatchFloor = stock.Floor;
                firstMismatchCurrentSeconds = currentFloorSeconds;
                firstMismatchStockSeconds = stockFloorSeconds;
                rows.AddRange(recent);
                rows.Add(row);
            }
            else if (stock.Floor <= firstMismatchFloor.Value + contextRadius)
            {
                rows.Add(row);
            }
        }
        else if (firstMismatchFloor is null && contextRadius > 0)
        {
            recent.Enqueue(row);
            while (recent.Count > contextRadius)
                recent.Dequeue();
        }
        else if (firstMismatchFloor is int first && stock.Floor <= first + contextRadius)
        {
            rows.Add(row);
        }
    }

    private static int ParsePlanets(string? planets, int fallback)
    {
        if (string.Equals(planets, "ThreePlanets", StringComparison.OrdinalIgnoreCase) || planets == "3")
            return 3;
        if (string.Equals(planets, "TwoPlanets", StringComparison.OrdinalIgnoreCase) || planets == "2")
            return 2;
        return fallback;
    }

    private static double GetTimeBetweenAngles(
        double entryAngle,
        double exitAngle,
        float speed,
        float bpm,
        bool isCw)
    {
        return GetAngleMoved(entryAngle, exitAngle, isCw) / PiStock *
               (60.0 / bpm / speed);
    }

    private static double GetAngleMoved(double entryAngle, double exitAngle, bool isCw)
    {
        double sign = isCw ? 1.0 : -1.0;
        return Mod((exitAngle - entryAngle) * sign, TwoPi);
    }

    private static double GetInverseAnglePerBeatMultiplanet(double planets)
    {
        return 3.1415926 * (planets - 2.0) / planets;
    }

    private static bool ApproximatelyFloor(double a, double b)
    {
        return Math.Abs(a - b) < 0.001;
    }

    private static double Mod(double value, double modulus)
    {
        double result = value % modulus;
        return result < 0.0 ? result + modulus : result;
    }

    private struct StockFloorState
    {
        public int Floor;
        public double EntryAngle;
        public double ExitAngle;
        public double NextEntryAngle;
        public float Speed;
        public bool IsCcw;
        public int NumPlanets;
        public bool MidSpin;
        public bool PreviousMidSpin;
        public string SetSpeedOffsets;
    }
}

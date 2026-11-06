namespace ExtremeEditor.Core;

/// <summary>
/// The clock stock ADOFAI uses to start VFX events.
///
/// UnityPlayer starts ADOFAI's Mono JIT with "-O=-float32", so the float32
/// expressions in scrLevelMaker.InstantiateFloatFloors,
/// scrLevelMaker.CalculateFloorEntryTimes and ffxPlusBase.SetStartTime are
/// evaluated in double precision and rounded only when stored into a float
/// local or field. scnGame.PrepVfx orders effects by that start time, so two
/// events a few nanoseconds apart (an angleOffset 360 event and the next
/// floors' angleOffset 0 event) run in the order this arithmetic gives.
///
/// The playback TimingMap keeps its own exact arithmetic. This clock follows
/// the same chart semantics as the TimingMap builders and only reproduces the
/// stock rounding, so the two never differ by more than float rounding.
/// </summary>
public static class StockVfxClock
{
    private const double PiStock = 3.1415927410125732;
    private const double TwoPiStock = 6.2831854820251465;
    private const double FirstEntryAngle = 4.71238899230957;
    private const double DegreesToRadians = 0.017453292f;
    private const double PiFloat = 3.1415927f;
    private const double FullTurnThreshold = 6.283184482025146;

    /// <summary>
    /// ffxPlusBase.SetStartTime:
    /// entryTime + degreeOffset * 0.017453292f / 3.1415927f * (60f / (bpm * speed)),
    /// where <paramref name="stockBpm"/> is bpm * speed.
    /// </summary>
    public static double AngleOffsetSeconds(double degreeOffset, double stockBpm)
    {
        double degrees = (float)degreeOffset;
        return degrees * DegreesToRadians / PiFloat * (60.0 / stockBpm);
    }

    internal sealed class Accumulator
    {
        private readonly double[] _entryAngles;
        private readonly double[] _exitAngles;
        private readonly float _baseBpm;
        private float _speed = 1f;
        private float _pauseBeats;
        private double _time;

        public Accumulator(double[] angles, int floorCount, double initialBpm)
        {
            _baseBpm = initialBpm > 0 ? (float)initialBpm : 100f;
            _entryAngles = new double[floorCount];
            _exitAngles = new double[floorCount];
            if (floorCount == 0)
                return;

            _entryAngles[0] = FirstEntryAngle;
            for (int floor = 0; floor < floorCount - 1; floor++)
            {
                double raw = floor < angles.Length ? angles[floor] : 0.0;
                double exit = IsMidSpin(raw)
                    ? (float)_entryAngles[floor]
                    : (-(double)(float)raw + 90.0) * DegreesToRadians;
                _exitAngles[floor] = exit;
                _entryAngles[floor + 1] = (exit + PiStock) % TwoPiStock;
            }
            _exitAngles[^1] = _entryAngles[^1] + PiStock;
        }

        /// <summary>Same SetSpeed semantics as the TimingMap builders.</summary>
        public void ApplySetSpeed(LevelAction action)
        {
            float newBpm;
            if (string.Equals(action.SpeedType, "Multiplier", StringComparison.OrdinalIgnoreCase) &&
                action.BpmMultiplier is double multiplier && multiplier > 0)
            {
                newBpm = (float)((double)_baseBpm * _speed * (float)multiplier);
            }
            else if (action.BeatsPerMinute is double target && target > 0)
            {
                newBpm = (float)target;
            }
            else
            {
                return;
            }
            _speed = (float)(newBpm / (double)_baseBpm);
        }

        public void AddPause(double durationBeats)
        {
            _pauseBeats = (float)(_pauseBeats + (double)(float)durationBeats);
        }

        /// <summary>Stock entry time of the floor about to be advanced.</summary>
        public double EntryTime => _time;

        /// <summary>Stock bpm * floor speed (ffxPlusBase.SetStartTime).</summary>
        public double Bpm => (double)_baseBpm * _speed;

        /// <summary>Advances by the floor's stock duration.</summary>
        public void Advance(
            int floor,
            bool isCcw,
            bool midSpin,
            bool threePlanets,
            bool previousMidSpin,
            int countdownTicks)
        {
            double speed = _speed;
            double bpm = _baseBpm;
            bool isCw = !isCcw;
            double halfTurn = TimeBetweenAngles(0.0, PiStock, speed, bpm, isCw: false);

            double offset = 0.0;
            if (floor > 0 && threePlanets)
            {
                double inverse = InverseAnglePerBeatMultiplanet(3);
                double sign = isCcw ? -1.0 : 1.0;
                offset = midSpin ? 0.0 : inverse * sign;
                if (previousMidSpin)
                    offset -= (TwoPiStock + inverse) * sign;
            }

            double entry = _entryAngles[floor] + offset;
            double exit = _exitAngles[floor] + (midSpin ? offset : 0.0);
            double moved = AngleMoved(entry, exit, isCw);
            double seconds = moved <= 1e-6 || moved >= FullTurnThreshold
                ? (midSpin ? 0.0 : 2.0 * halfTurn)
                : moved / PiStock * (60.0 / bpm / speed);

            if (floor == 0)
            {
                // Stock: scrConductor.crotchetAtStart * (adjustedCountdownTicks - 1)
                // with crotchetAtStart = 60f / levelBpm. The TimingMap builders
                // count the countdown at floor 0's speed instead (they differ
                // only when floor 0 has a SetSpeed); follow them so VFX stay on
                // the playback timeline.
                double crotchet = 60.0 / bpm / speed;
                seconds = crotchet * (float)Math.Max(0, countdownTicks - 1) + seconds;
            }

            _time += seconds;
            _time += (double)_pauseBeats * halfTurn;
            _pauseBeats = 0f;
        }

        private static bool IsMidSpin(double raw) => Math.Abs(raw - 999.0) < 0.000001;

        // scrMisc.GetTimeBetweenAngles.
        private static double TimeBetweenAngles(
            double entry,
            double exit,
            double speed,
            double bpm,
            bool isCw) =>
            AngleMoved(entry, exit, isCw) / PiStock * (60.0 / bpm / speed);

        // scrMisc.GetAngleMoved with scrMisc.mod((x % m + m) % m).
        private static double AngleMoved(double entry, double exit, bool isCw)
        {
            double value = (exit - entry) * (isCw ? 1.0 : -1.0);
            return (value % TwoPiStock + TwoPiStock) % TwoPiStock;
        }

        // scrMisc.GetInverseAnglePerBeatMultiplanet.
        private static double InverseAnglePerBeatMultiplanet(double planets) =>
            3.1415926 * (planets - 2.0) / planets;
    }
}

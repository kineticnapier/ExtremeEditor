using System.Numerics;
using ExtremeEditor.Core;
using ExtremeEditor.Rendering;

namespace ExtremeEditor.Wpf.Native;

internal sealed class NativeLevelSnapshot
{
    public required NativeFloor[] Floors { get; init; }
    public required NativeGeometry[] Geometries { get; init; }
    public required NativePoint[] Points { get; init; }
    public required float BoundsLeft { get; init; }
    public required float BoundsTop { get; init; }
    public required float BoundsRight { get; init; }
    public required float BoundsBottom { get; init; }
}

internal static class NativeLevelSnapshotBuilder
{
    private const float TwoPi = MathF.PI * 2f;

    public static NativeLevelSnapshot Build(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);

        Vector2[] positions = level.Positions;
        var floors = new NativeFloor[positions.Length];
        var geometries = new List<NativeGeometry>();
        var points = new List<NativePoint>(4096);
        var geometryIds = new Dictionary<GeometryKey, uint>();

        for (int floor = 0; floor < positions.Length; floor++)
        {
            GetFloorAngles(floor, positions, out float entryAngle, out float exitAngle);
            bool midSpin = floor < level.Angles.Length && Math.Abs(level.Angles[floor] - 999.0) < 0.000001;
            float delta = Mod(exitAngle - entryAngle, TwoPi);
            var key = new GeometryKey((int)MathF.Round(delta * 100_000f), midSpin);

            if (!geometryIds.TryGetValue(key, out uint geometryId))
            {
                FloorGeometry source = AdoFaiFloorGeometryBuilder.Get(0f, delta, midSpin);
                geometryId = checked((uint)geometries.Count);
                uint pointOffset = checked((uint)points.Count);

                foreach (Vector2 point in source.Main)
                {
                    points.Add(new NativePoint
                    {
                        X = point.X,
                        Y = point.Y
                    });
                }

                geometries.Add(new NativeGeometry
                {
                    PointOffset = pointOffset,
                    PointCount = checked((uint)source.Main.Length)
                });
                geometryIds.Add(key, geometryId);
            }

            Vector2 position = positions[floor];
            floors[floor] = new NativeFloor
            {
                X = position.X,
                Y = position.Y,
                EntryAngle = entryAngle,
                GeometryId = geometryId
            };
        }

        WorldRect bounds = level.Bounds;
        return new NativeLevelSnapshot
        {
            Floors = floors,
            Geometries = geometries.ToArray(),
            Points = points.ToArray(),
            BoundsLeft = bounds.Left,
            BoundsTop = bounds.Top,
            BoundsRight = bounds.Right,
            BoundsBottom = bounds.Bottom
        };
    }

    private static void GetFloorAngles(int floor, Vector2[] positions, out float entryAngle, out float exitAngle)
    {
        Vector2 incoming = Vector2.Zero;
        Vector2 outgoing = Vector2.Zero;

        if (floor > 0)
            incoming = positions[floor - 1] - positions[floor];
        if (floor + 1 < positions.Length)
            outgoing = positions[floor + 1] - positions[floor];

        if (incoming.LengthSquared() < 0.000001f && outgoing.LengthSquared() >= 0.000001f)
            incoming = -outgoing;
        if (outgoing.LengthSquared() < 0.000001f && incoming.LengthSquared() >= 0.000001f)
            outgoing = -incoming;

        entryAngle = incoming.LengthSquared() < 0.000001f
            ? MathF.PI
            : MathF.Atan2(incoming.Y, incoming.X);
        exitAngle = outgoing.LengthSquared() < 0.000001f
            ? 0f
            : MathF.Atan2(outgoing.Y, outgoing.X);
    }

    private static float Mod(float value, float modulus)
    {
        float result = value % modulus;
        return result < 0f ? result + modulus : result;
    }

    private readonly record struct GeometryKey(int Delta, bool MidSpin);
}

using System.Numerics;

namespace ExtremeEditor.Core;

public static class PathBuilder
{
    // Stock scrController.tileSize is baseFloorDimensions.x * 2. The default
    // long floor is 0.75 units from its center to either end, so consecutive
    // floor centers are 1.5 units apart. Using a unit step made adjacent floor
    // meshes overlap by half a tile and visually bite into one another.
    public const float DefaultLongTileSize = 1.5f;

    public static Vector2[] BuildPositions(ReadOnlySpan<double> angles) =>
        BuildPositions(angles, ReadOnlySpan<float>.Empty);

    public static Vector2[] BuildPositions(
        ReadOnlySpan<double> angles,
        ReadOnlySpan<float> radiusScales)
    {
        if (angles.Length == 0)
            return [Vector2.Zero];

        // ADOFAI creates floor 0 plus one floor for every angle entry.
        var positions = new Vector2[angles.Length + 1];
        Vector2 current = Vector2.Zero;

        // scrLevelMaker initializes floor 0 with this entry angle. It matters for
        // 999/midspin segments, whose exit angle reuses the current entry angle.
        double entryAngle = 4.71238899230957;

        for (int i = 0; i < angles.Length; i++)
        {
            double angle = angles[i];
            double exitAngle = Math.Abs(angle - 999.0) < 0.000001
                ? entryAngle
                : (-angle + 90.0) * Math.PI / 180.0;

            // ScaleRadius belongs to the current floor and affects the outgoing
            // radius to the next floor. Stock uses prevFloor.radiusScale when it
            // places that next floor, so the scale for edge i -> i+1 is index i.
            float radiusScale = i < radiusScales.Length && float.IsFinite(radiusScales[i])
                ? radiusScales[i]
                : 1.0f;
            float step = DefaultLongTileSize * radiusScale;

            // Mirrors scrMisc.getVectorFromAngle(exitAngle, tileSize):
            // (sin(a), cos(a)) * tileSize * radiusScale.
            current += new Vector2(
                (float)Math.Sin(exitAngle) * step,
                (float)Math.Cos(exitAngle) * step);
            positions[i + 1] = current;

            entryAngle = PositiveMod(exitAngle + Math.PI, Math.PI * 2.0);
        }

        return positions;
    }

    public static WorldRect CalculateBounds(ReadOnlySpan<Vector2> positions)
    {
        if (positions.Length == 0)
            return new WorldRect(-1, -1, 1, 1);

        float minX = positions[0].X;
        float maxX = minX;
        float minY = positions[0].Y;
        float maxY = minY;

        for (int i = 1; i < positions.Length; i++)
        {
            Vector2 p = positions[i];
            minX = Math.Min(minX, p.X);
            maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y);
            maxY = Math.Max(maxY, p.Y);
        }

        if (maxX - minX < 1f) { minX -= .5f; maxX += .5f; }
        if (maxY - minY < 1f) { minY -= .5f; maxY += .5f; }

        return new WorldRect(minX, minY, maxX, maxY);
    }

    private static double PositiveMod(double value, double modulus) =>
        (value % modulus + modulus) % modulus;
}

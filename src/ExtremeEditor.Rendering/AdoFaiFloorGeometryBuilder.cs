using System.Numerics;

namespace ExtremeEditor.Rendering;

/// <summary>
/// Generates the visible outline of an ADOFAI floor from its incoming/outgoing rays.
/// The geometry follows the current game's FloorMesh corner construction, but keeps
/// only the outer floor and shadow polygons needed by the interim GDI renderer.
/// </summary>
public static class AdoFaiFloorGeometryBuilder
{
    private const float Width = 0.4125f;
    private const float Length = 0.75f;
    private const float ShadowWidth = 0.11f;
    private const float Epsilon = 0.0001f;
    private const float TwoPi = MathF.PI * 2f;

    private static readonly Dictionary<GeometryKey, FloorGeometry> Cache = new();

    public static FloorGeometry Get(float entryAngle, float exitAngle, bool midSpin)
    {
        float delta = ModAngle(exitAngle - entryAngle);
        int curvaturePoints = midSpin ? 3 : 40;
        var key = new GeometryKey((int)MathF.Round(delta * 100_000f), curvaturePoints);
        if (!Cache.TryGetValue(key, out FloorGeometry? geometry))
        {
            geometry = Build(0f, delta, curvaturePoints);
            Cache[key] = geometry;
        }
        return geometry;
    }

    private static FloorGeometry Build(float angle0, float angle1, int curvaturePoints)
    {
        angle0 = ModAngle(angle0);
        angle1 = ModAngle(angle1);
        if (ModAngle(angle1 - angle0) >= MathF.PI)
            (angle0, angle1) = (angle1, angle0);

        float shortAngle = SmallestAngle(angle1, angle0);
        bool zeroAngle = shortAngle < Epsilon;
        bool piAngle = MathF.Abs(shortAngle - MathF.PI) < Epsilon;

        Vector2 origin = Vector2.Zero;
        float length = Length;
        if (zeroAngle)
        {
            origin = Add(origin, angle1, -length / 12f);
            length *= 2f / 3f;
        }

        Vector2 startCenter = Add(origin, angle0, length);
        float startMoreAngle = angle0 + MathF.PI / 2f;
        Vector2 startMore = Add(startCenter, startMoreAngle, Width);
        Vector2 startMoreRay = Add(startMore, startMoreAngle + MathF.PI / 2f, Width * 0.01f);
        float startLessAngle = angle0 - MathF.PI / 2f;
        Vector2 startLess = Add(startCenter, startLessAngle, Width);
        Vector2 startLessRay = Add(startLess, startLessAngle - MathF.PI / 2f, Width * 0.01f);

        Vector2 endCenter = Add(origin, angle1, length);
        float endMoreAngle = angle1 + MathF.PI / 2f;
        Vector2 endMore = Add(endCenter, endMoreAngle, Width);
        Vector2 endMoreRay = Add(endMore, endMoreAngle + MathF.PI / 2f, Width * 0.01f);
        float endLessAngle = angle1 - MathF.PI / 2f;
        Vector2 endLess = Add(endCenter, endLessAngle, Width);
        Vector2 endLessRay = Add(endLess, endLessAngle - MathF.PI / 2f, Width * 0.01f);

        Vector2 ccwIntersection = zeroAngle || piAngle
            ? origin
            : LineIntersection(startMore, startMoreRay, endLess, endLessRay);
        Vector2 cwIntersection = zeroAngle || piAngle
            ? origin
            : LineIntersection(startLess, startLessRay, endMore, endMoreRay);

        float roundness = CornerRoundness(shortAngle);
        float angleDifference = ModAngle(angle1 - angle0);
        Vector2 cwCenter = Vector2.Lerp(cwIntersection, origin, roundness);
        Vector2 ccwCenter = Vector2.Lerp(ccwIntersection, origin, roundness);
        float cornerRadius = Width * roundness;

        List<Vector2> cwCornerPoints = angleDifference < 2.0942953f
            ? CreateCircleArc(cwCenter, ModAngle(endMoreAngle), ModAngle(startLessAngle), cornerRadius, curvaturePoints)
            : [cwIntersection];
        List<Vector2> ccwCornerPoints = angleDifference > 4.3634233f
            ? CreateCircleArc(ccwCenter, ModAngle(startMoreAngle), ModAngle(endLessAngle), cornerRadius, curvaturePoints)
            : [ccwIntersection];

        Vector2 spike = angleDifference < MathF.PI ? ccwIntersection : cwIntersection;
        bool pathOverlaps = Vector2.Distance(origin, startMore) < Vector2.Distance(origin, spike) && !zeroAngle && !piAngle;
        Vector2[]? overlapping = null;
        if (pathOverlaps)
        {
            overlapping =
            [
                LineIntersection(startLess, startMore, endLess, endLessRay),
                LineIntersection(startLess, startMore, endLess, endMore),
                LineIntersection(endLess, endMore, startMore, startMoreRay)
            ];
        }

        var main = new List<Vector2>(curvaturePoints + 8);
        if (zeroAngle)
        {
            main.Add(startMore);
            main.AddRange(cwCornerPoints);
            main.Add(endLess);
        }
        else if (piAngle)
        {
            main.AddRange([startMore, endLess, endMore, startLess]);
        }
        else if (!pathOverlaps)
        {
            main.Add(startMore);
            main.AddRange(ccwCornerPoints);
            main.Add(endLess);
            main.Add(endMore);
            main.AddRange(cwCornerPoints);
            main.Add(startLess);
        }
        else
        {
            main.AddRange([startMore, overlapping![2], endMore]);
            main.AddRange(cwCornerPoints);
            main.AddRange([startLess, overlapping[0], endLess, overlapping[1]]);
        }

        var shadows = new List<Vector2[]>(2);
        if (piAngle)
        {
            AddShadowBand(shadows,
                [Add(startMore, startMoreAngle, ShadowWidth), Add(endLess, endLessAngle, ShadowWidth)],
                [startMore, endLess]);
            AddShadowBand(shadows,
                [Add(endMore, endMoreAngle, ShadowWidth), Add(startLess, startLessAngle, ShadowWidth)],
                [endMore, startLess]);
        }
        else
        {
            if (!pathOverlaps && !zeroAngle)
            {
                Vector2 diamondTip = LineIntersection(endCenter, endLess, startCenter, startMore);
                if (Vector2.Distance(diamondTip, endLess) < ShadowWidth)
                {
                    AddShadowBand(shadows,
                        [startMore, diamondTip, endLess],
                        [ccwIntersection]);
                }
                else
                {
                    Vector2 outerStart = Add(startMore, startMoreAngle, ShadowWidth);
                    Vector2 outerEnd = Add(endLess, endLessAngle, ShadowWidth);
                    Vector2 outerIntersection = LineIntersection(
                        outerStart, outerStart + (startMoreRay - startMore),
                        outerEnd, outerEnd + (endLessRay - endLess));
                    AddShadowBand(shadows,
                        [outerStart, outerIntersection, outerEnd],
                        [startMore, ccwIntersection, endLess]);
                }
            }

            Vector2 cwOuterStart = Add(endMore, endMoreAngle, ShadowWidth);
            Vector2 cwOuterEnd = Add(startLess, startLessAngle, ShadowWidth);
            bool rounded = cwCornerPoints.Count > 1;
            float shadowRadius = rounded ? ShadowWidth + cornerRadius : ShadowWidth;
            List<Vector2> outerArc = CreateCircleArc(
                cwCenter, ModAngle(endMoreAngle), ModAngle(startLessAngle), shadowRadius, curvaturePoints);
            var outer = new List<Vector2>(outerArc.Count + 2) { cwOuterStart };
            outer.AddRange(outerArc);
            outer.Add(cwOuterEnd);
            var inner = new List<Vector2>(cwCornerPoints.Count + 2) { endMore };
            inner.AddRange(cwCornerPoints);
            inner.Add(startLess);
            AddShadowBand(shadows, outer, inner);
        }

        return new FloorGeometry(main.ToArray(), shadows.ToArray());
    }

    private static void AddShadowBand(List<Vector2[]> destination, IReadOnlyList<Vector2> outer, IReadOnlyList<Vector2> inner)
    {
        if (outer.Count == 0) return;
        var polygon = new Vector2[outer.Count + inner.Count];
        int p = 0;
        for (int i = 0; i < outer.Count; i++) polygon[p++] = outer[i];
        for (int i = inner.Count - 1; i >= 0; i--) polygon[p++] = inner[i];
        destination.Add(polygon);
    }

    private static List<Vector2> CreateCircleArc(Vector2 center, float angleA, float angleB, float radius, int pointCount)
    {
        if (angleA > angleB) angleA -= TwoPi;
        pointCount = Math.Max(pointCount, 3);
        var result = new List<Vector2>(pointCount);
        for (int i = 0; i < pointCount; i++)
        {
            float t = (float)i / (pointCount - 1);
            float angle = angleA + (angleB - angleA) * t;
            result.Add(Add(center, angle, radius));
        }
        return result;
    }

    private static float CornerRoundness(float angle)
    {
        float value = 0f;
        if (angle < 0.08726646f)
            value = 1f;
        else if (angle < MathF.PI / 6f)
            value = Lerp(1f, 0.83f, MathF.Pow((angle - 0.08726646f) / (MathF.PI * 5f / 36f), 0.5f));
        else if (angle < MathF.PI / 4f)
            value = Lerp(0.83f, 0.77f, (angle - MathF.PI / 6f) / (MathF.PI / 12f));
        else if (angle < MathF.PI / 2f)
            value = Lerp(0.77f, 0.15f, MathF.Pow((angle - MathF.PI / 4f) / (MathF.PI / 4f), 0.7f));
        else if (angle < MathF.PI * 2f / 3f)
            value = Lerp(0.15f, 0f, MathF.Pow((angle - MathF.PI / 2f) / (MathF.PI / 6f), 0.5f));
        return value < 0.001f ? 0f : value;
    }

    private static Vector2 LineIntersection(Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1)
    {
        Vector2 x = new(a0.X - a1.X, b0.X - b1.X);
        Vector2 y = new(a0.Y - a1.Y, b0.Y - b1.Y);
        float d = Determinant(x, y);
        if (MathF.Abs(d) < 1e-7f)
            return (a0 + b0) * 0.5f;
        Vector2 q = new(Determinant(a0, a1), Determinant(b0, b1));
        return new Vector2(Determinant(q, x) / d, Determinant(q, y) / d);
    }

    private static float Determinant(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
    private static Vector2 Add(Vector2 p, float angle, float distance) =>
        p + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * distance;
    private static float SmallestAngle(float a, float b) => Math.Min(ModAngle(b - a), ModAngle(a - b));
    private static float ModAngle(float a) => (a % TwoPi + TwoPi) % TwoPi;
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private readonly record struct GeometryKey(int Delta, int CurvaturePoints);
}

public sealed record FloorGeometry(Vector2[] Main, Vector2[][] Shadows);

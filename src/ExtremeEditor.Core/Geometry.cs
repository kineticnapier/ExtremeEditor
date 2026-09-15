using System.Numerics;

namespace ExtremeEditor.Core;

public readonly record struct WorldRect(float Left, float Top, float Right, float Bottom)
{
    public float Width => Right - Left;
    public float Height => Bottom - Top;

    public WorldRect Inflate(float amount) =>
        new(Left - amount, Top - amount, Right + amount, Bottom + amount);

    public bool Contains(Vector2 p) =>
        p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;
}

public readonly record struct FloorPoint(int Index, Vector2 Position, double Angle);

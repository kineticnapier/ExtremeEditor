using System.Numerics;

namespace ExtremeEditor.Rendering;

public readonly record struct FloorMeshVertex(Vector2 Position, Vector2 Uv, Vector2 Uv2);

/// <summary>
/// Clean-room representation of the standard straight floor mesh observed through
/// EditorQoL's runtime Asset Probe. This is data, not Unity runtime code.
/// </summary>
public static class AdoFaiFloorMesh
{
    public static readonly FloorMeshVertex[] StraightVertices =
    [
        V( 0.75f,  0.4125f, 1f), V(-0.75f,  0.4125f, 1f),
        V(-0.75f, -0.4125f, 1f), V( 0.75f, -0.4125f, 1f),
        V( 0.3375f, 0f, 0f), V(-0.3375f, 0f, 0f),
        V(-0.3375f, 0f, 0f), V( 0.3375f, 0f, 0f),
        V(-0.75f, -0.5225f, 2f), V( 0.75f, -0.5225f, 2f),
        V(-0.75f, -0.4125f, 1f), V( 0.75f, -0.4125f, 1f),
        V( 0.75f,  0.5225f, 2f), V(-0.75f,  0.5225f, 2f),
        V( 0.75f,  0.4125f, 1f), V(-0.75f,  0.4125f, 1f)
    ];

    public static readonly int[] StraightTriangles =
    [
        0, 4, 1, 1, 4, 5, 1, 5, 2, 2, 5, 6,
        2, 6, 3, 3, 6, 7, 3, 7, 0, 0, 7, 4,
        8, 10, 9, 9, 10, 11,
        12, 14, 13, 13, 14, 15
    ];

    public static readonly int[] MainOutline = [0, 1, 2, 3];
    public static readonly int[] BottomShadowOutline = [8, 9, 11, 10];
    public static readonly int[] TopShadowOutline = [12, 13, 15, 14];

    private static FloorMeshVertex V(float x, float y, float uvY) =>
        new(new Vector2(x, y), new Vector2(0.5f, uvY), new Vector2(x, y));
}

using System.Numerics;
using System.Windows;

namespace ExtremeEditor.Wpf;

internal sealed record RasterChunkRequest(
    RasterChunkKey Key,
    Rect WorldRect,
    Rect ScreenRect,
    int PixelWidth,
    int PixelHeight,
    float Zoom,
    int[] CandidateFloorsDescending,
    Vector2[] Positions,
    double[] Angles,
    int Priority = 2);

namespace ExtremeEditor.Core;

public interface IChartFrameRenderer : IDisposable
{
    void Load(string adofaiPath);
    RenderedFrame Render(double chartTimeSeconds);
    RenderedFrame Render(double sceneTimeSeconds, double visualTimeSeconds);
    void Reset();
}

/// <summary>
/// Optional streaming contract for consumers that finish using each frame before
/// requesting the next one. The returned RGB storage may be overwritten by the
/// next render call and must not be retained by the consumer.
/// </summary>
public interface IBorrowedChartFrameRenderer
{
    RenderedFrame RenderBorrowed(double sceneTimeSeconds, double visualTimeSeconds);
}

public sealed record RenderedFrame(int Width, int Height, byte[] Rgb)
{
    public int Stride => checked(Width * 3);
}

namespace ExtremeEditor.Core;

public interface IChartFrameRenderer : IDisposable
{
    void Load(string adofaiPath);
    RenderedFrame Render(double chartTimeSeconds);
    RenderedFrame Render(double sceneTimeSeconds, double visualTimeSeconds);
    void Reset();
}

public sealed record RenderedFrame(int Width, int Height, byte[] Rgb)
{
    public int Stride => checked(Width * 3);
}

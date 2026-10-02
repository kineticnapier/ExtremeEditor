using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

public sealed class NativeChartFrameRenderer : IChartFrameRenderer
{
    public const int DefaultWidth = 320;
    public const int DefaultHeight = 180;

    private readonly int _width;
    private readonly int _height;
    private NativeHeadlessRendererSession? _session;
    private LevelDocument? _level;
    private TimingMap? _timingMap;

    public NativeChartFrameRenderer(int width = DefaultWidth, int height = DefaultHeight)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        _width = width;
        _height = height;
    }

    public int Width => _width;
    public int Height => _height;
    public bool IsLoaded => _session is not null;

    public void Load(string adofaiPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adofaiPath);
        string fullPath = Path.GetFullPath(adofaiPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("ADOFAI chart was not found.", fullPath);

        LevelDocument level = AdoFaiLoader.Load(fullPath).Document;
        TimingMap timingMap = FlatTimingMapBuilder.Build(level);
        NativeHeadlessRendererSession next = NativeHeadlessRendererSession.Create(
            level,
            timingMap,
            _width,
            _height);

        NativeHeadlessRendererSession? previous = _session;
        _session = next;
        _level = level;
        _timingMap = timingMap;
        previous?.Dispose();
    }

    public RenderedFrame Render(double chartTimeSeconds) =>
        Render(chartTimeSeconds, chartTimeSeconds);

    public RenderedFrame Render(double sceneTimeSeconds, double visualTimeSeconds)
    {
        NativeHeadlessRendererSession session = _session
            ?? throw new InvalidOperationException("No chart is loaded.");

        NativeRgbFrame frame = session.Render(sceneTimeSeconds, visualTimeSeconds);
        return new RenderedFrame(frame.Width, frame.Height, frame.Rgb);
    }

    public void Reset()
    {
        if (_level is null || _timingMap is null)
            return;

        NativeHeadlessRendererSession next = NativeHeadlessRendererSession.Create(
            _level,
            _timingMap,
            _width,
            _height);
        NativeHeadlessRendererSession? previous = _session;
        _session = next;
        previous?.Dispose();
    }

    public void Dispose()
    {
        NativeHeadlessRendererSession? session = _session;
        _session = null;
        _level = null;
        _timingMap = null;
        session?.Dispose();
        GC.SuppressFinalize(this);
    }
}

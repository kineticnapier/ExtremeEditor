namespace ExtremeEditor.Wpf;

public sealed partial class LevelViewport
{
    public void ReloadAssets()
    {
        _floorRenderer.ReloadAssets();
        _iconRenderer.ReloadAssets();
        ResetStaticScene();
        EnsureSceneCoverage();
        RenderPlaybackVisual();
        InvalidateVisual();
    }
}

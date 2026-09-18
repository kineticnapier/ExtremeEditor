namespace ExtremeEditor.App;

public sealed partial class MainForm
{
    private bool _compactPlaybackDisplayAttached;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_compactPlaybackDisplayAttached)
            return;

        _compactPlaybackDisplayAttached = true;
        _playbackTimer.Tick += (_, _) => UpdateCompactPlaybackDisplay();
        UpdateCompactPlaybackDisplay();
    }

    private void UpdateCompactPlaybackDisplay()
    {
        if (!_audio.IsLoaded || (!_audio.IsPlaying && !_audio.IsPaused))
            return;

        _playTime.Text =
            $"{_audio.Position:mm\\:ss\\.fff}/{_audio.Duration:mm\\:ss\\.fff}";
    }
}

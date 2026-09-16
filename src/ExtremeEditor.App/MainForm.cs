using System.Diagnostics;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using ExtremeEditor.Rendering;

namespace ExtremeEditor.App;

public sealed class MainForm : Form
{
    private const int MaxHitSoundsPerFrame = 128;
    private const double HitSoundScheduleLeadSeconds = 0.020;
    private const double HitSoundLateWindowSeconds = 0.050;

    private readonly LevelCanvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly AudioPlayer _audio = new();
    private readonly HitSoundPlayer _hitSounds = new();
    private readonly System.Windows.Forms.Timer _playbackTimer = new() { Interval = 16 };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _renderStatus = new();
    private readonly ToolStripButton _open = new("Open");
    private readonly ToolStripButton _synthetic = new("Synthetic 238k");
    private readonly ToolStripButton _frame = new("Frame");
    private readonly ToolStripButton _play = new("Play");
    private readonly ToolStripButton _stop = new("Stop");
    private readonly ToolStripButton _follow = new("Follow Player") { CheckOnClick = true, Checked = true };
    private readonly ToolStripLabel _playTime = new("--:--.---");
    private readonly ToolStripButton _rotateLeft = new("-15°");
    private readonly ToolStripButton _rotateRight = new("+15°");
    private readonly ToolStripButton _saveAs = new("Save As");
    private readonly ToolStripButton _importAssets = new("Import Probe Assets");
    private readonly ToolStripButton _importIcons = new("Import Icon Catalog");
    private readonly ToolStripButton _importHitsounds = new("Import Hitsounds");
    private readonly ToolStripButton _floorPreview = new("Floor Preview") { CheckOnClick = true, Checked = true };
    private readonly ToolStripButton _benchmark = new("Benchmark viewport");

    private TimingMap? _timingMap;
    private HitSoundTimeline? _hitSoundTimeline;
    private int _nextHitFloor = 1;
    private double _lastHitAudioSeconds = double.NaN;

    public MainForm(string? initialFile)
    {
        Text = $"ExtremeEditor {EditorVersion.Current}";
        Width = 1400;
        Height = 850;
        KeyPreview = true;

        var tools = new ToolStrip();
        tools.Items.AddRange([_open, _synthetic, new ToolStripSeparator(), _frame,
            new ToolStripSeparator(), _play, _stop, _follow, _playTime,
            new ToolStripSeparator(), _rotateLeft, _rotateRight, _saveAs,
            new ToolStripSeparator(), _importAssets, _importIcons, _importHitsounds, _floorPreview,
            new ToolStripSeparator(), _benchmark]);

        var statusStrip = new StatusStrip();
        statusStrip.Items.AddRange([_status, _renderStatus]);

        Controls.Add(_canvas);
        Controls.Add(tools);
        Controls.Add(statusStrip);

        _open.Click += (_, _) => OpenLevel();
        _synthetic.Click += (_, _) => LoadSynthetic();
        _frame.Click += (_, _) => _canvas.FrameAll();
        _play.Click += (_, _) => TogglePlayback();
        _stop.Click += (_, _) => StopPlayback();
        _follow.CheckedChanged += (_, _) => _canvas.FollowPlayback = _follow.Checked;
        _rotateLeft.Click += (_, _) => RotateSelected(-15);
        _rotateRight.Click += (_, _) => RotateSelected(15);
        _saveAs.Click += (_, _) => SaveAs();
        _importAssets.Click += (_, _) => ImportProbeAssets();
        _importIcons.Click += (_, _) => ImportIconCatalog();
        _importHitsounds.Click += (_, _) => ImportHitSounds();
        _floorPreview.CheckedChanged += (_, _) => _canvas.UseFloorPreview = _floorPreview.Checked;
        _benchmark.Click += (_, _) => BenchmarkViewport();
        _canvas.DiagnosticsChanged += UpdateRenderStatus;
        _canvas.FollowPlaybackChanged += value =>
        {
            if (_follow.Checked != value)
                _follow.Checked = value;
        };
        _canvas.PlaybackPoseProvider = GetLivePlaybackPose;
        _canvas.FollowPlayback = _follow.Checked;
        _playbackTimer.Tick += (_, _) => UpdatePlaybackFrame();
        _playbackTimer.Start();

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space)
            {
                TogglePlayback();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.Escape)
            {
                StopPlayback();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.F) _canvas.FrameAll();
            if (e.KeyCode == Keys.O) OpenLevel();
            if (e.KeyCode == Keys.OemOpenBrackets) RotateSelected(-15);
            if (e.KeyCode == Keys.OemCloseBrackets) RotateSelected(15);
            if (e.Control && e.KeyCode == Keys.S) SaveAs();
        };

        Shown += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(initialFile) && File.Exists(initialFile))
                LoadLevel(initialFile);
            else
                LoadSynthetic();
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _playbackTimer.Dispose();
            _hitSounds.Dispose();
            _audio.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OpenLevel()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "ADOFAI levels (*.adofai)|*.adofai|All files (*.*)|*.*",
            Title = "Open ADOFAI level"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            LoadLevel(dialog.FileName);
    }

    private void LoadLevel(string path)
    {
        try
        {
            StopPlayback();
            _audio.Unload();
            UseWaitCursor = true;
            _status.Text = "Loading…";
            Application.DoEvents();

            var sw = Stopwatch.StartNew();
            LoadResult loaded = AdoFaiLoader.Load(path);
            var indexWatch = Stopwatch.StartNew();
            var index = new SpatialGridIndex(loaded.Document.Positions);
            indexWatch.Stop();

            _canvas.SetLevel(loaded.Document, index);
            _timingMap = TimingMapBuilder.Build(loaded.Document);
            _hitSoundTimeline = HitSoundTimelineBuilder.Build(loaded.Document);
            ResetHitSoundCursor();
            string audioState = LoadSongForLevel(loaded.Document);
            sw.Stop();

            if (Math.Abs(loaded.Document.PitchPercent - 100.0) > 0.001)
                audioState += $" | pitch {loaded.Document.PitchPercent:0.##}% not yet applied to audio";

            _status.Text =
                $"{Path.GetFileName(path)} | floors {loaded.Document.FloorCount:N0} | " +
                $"actions {loaded.Document.ActionCount:N0} | " +
                $"read {loaded.Metrics.Read.TotalMilliseconds:N1} ms | " +
                $"parse {loaded.Metrics.Parse.TotalMilliseconds:N1} ms | " +
                $"path {loaded.Metrics.BuildPath.TotalMilliseconds:N1} ms | " +
                $"index {indexWatch.Elapsed.TotalMilliseconds:N1} ms | " +
                $"total {sw.Elapsed.TotalMilliseconds:N1} ms | {audioState} | {_hitSounds.AssetSummary}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Open failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _status.Text = "Open failed";
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private string LoadSongForLevel(LevelDocument level)
    {
        string? songPath = level.ResolveSongPath();
        if (songPath is null)
            return "audio unavailable";
        if (!File.Exists(songPath))
            return $"audio missing: {Path.GetFileName(songPath)}";

        try
        {
            _audio.Load(songPath);
            return $"audio {Path.GetFileName(songPath)}";
        }
        catch (Exception ex)
        {
            _audio.Unload();
            return $"audio load failed: {ex.Message}";
        }
    }

    private void TogglePlayback()
    {
        LevelDocument? level = _canvas.Level;
        if (level is null || !_audio.IsLoaded || _timingMap is null)
            return;

        if (_audio.IsPlaying)
        {
            _audio.Pause();
            _play.Text = "Play";
            UpdatePlaybackFrame();
            return;
        }

        if (_audio.IsStopped && _canvas.SelectedFloor >= 0)
        {
            double chartTime = _timingMap.GetEntryTime(_canvas.SelectedFloor);
            double audioTime = PlaybackClock.ChartToAudioTime(level, chartTime);
            _audio.Seek(TimeSpan.FromSeconds(Math.Max(0, audioTime)));
        }

        ResetHitSoundCursor();
        _audio.Play();
        _play.Text = _audio.IsPlaying ? "Pause" : "Play";
        UpdatePlaybackFrame();
    }

    private void StopPlayback()
    {
        _audio.Stop();
        _play.Text = "Play";
        _playTime.Text = "--:--.---";
        _canvas.SetPlaybackPose(null);
        _nextHitFloor = 1;
        _lastHitAudioSeconds = double.NaN;
    }

    private PlaybackPose? GetLivePlaybackPose()
    {
        LevelDocument? level = _canvas.Level;
        if (level is null || _timingMap is null || !_audio.IsLoaded)
            return null;
        if (!_audio.IsPlaying && !_audio.IsPaused)
            return null;

        double audioSeconds = _audio.Position.TotalSeconds;
        ProcessHitSounds(audioSeconds);
        double chartTime = PlaybackClock.AudioToChartTime(level, audioSeconds);
        return _timingMap.GetPose(level, chartTime);
    }

    private void UpdatePlaybackFrame()
    {
        PlaybackPose? pose = GetLivePlaybackPose();
        _canvas.SetPlaybackPose(pose);

        if (pose is not PlaybackPose current)
        {
            if (!_audio.IsPlaying)
                _play.Text = "Play";
            return;
        }

        string phase = current.IsPreStart ? "offset" : $"floor {current.Floor:N0}";
        _playTime.Text = $"{_audio.Position:mm\\:ss\\.fff} | {phase}";
        if (!_audio.IsPlaying)
            _play.Text = "Play";
    }

    private void ResetHitSoundCursor()
    {
        LevelDocument? level = _canvas.Level;
        if (level is null || _timingMap is null || !_audio.IsLoaded)
        {
            _nextHitFloor = 1;
            _lastHitAudioSeconds = double.NaN;
            return;
        }

        double audioSeconds = _audio.Position.TotalSeconds;
        double chartTime = PlaybackClock.AudioToChartTime(level, audioSeconds);
        PlaybackPose pose = _timingMap.GetPose(level, chartTime);
        int floor = Math.Max(1, pose.Floor);

        while (floor < level.FloorCount)
        {
            HitSoundState state = _hitSoundTimeline?.GetStateAtFloor(floor) ?? HitSoundState.FromLevel(level);
            double targetAudio = GetHitSoundAudioTime(level, floor, state);
            if (targetAudio >= audioSeconds - HitSoundLateWindowSeconds)
                break;
            floor++;
        }

        _nextHitFloor = floor;
        _lastHitAudioSeconds = audioSeconds;
    }

    private void ProcessHitSounds(double audioSeconds)
    {
        LevelDocument? level = _canvas.Level;
        if (!_audio.IsPlaying || level is null || _timingMap is null || _hitSoundTimeline is null ||
            _hitSounds.LoadedCount == 0)
        {
            _lastHitAudioSeconds = audioSeconds;
            return;
        }

        if (double.IsNaN(_lastHitAudioSeconds) || audioSeconds + HitSoundLateWindowSeconds < _lastHitAudioSeconds)
            ResetHitSoundCursor();

        int processed = 0;
        while (_nextHitFloor < level.FloorCount && processed < MaxHitSoundsPerFrame)
        {
            int floor = _nextHitFloor;
            HitSoundState state = _hitSoundTimeline.GetStateAtFloor(floor);
            double targetAudio = GetHitSoundAudioTime(level, floor, state);
            if (targetAudio > audioSeconds + HitSoundScheduleLeadSeconds)
                break;

            bool recentEnough = targetAudio >= _lastHitAudioSeconds - HitSoundLateWindowSeconds;
            bool isMidSpin = (uint)floor < (uint)_timingMap.Floors.Count && _timingMap.Floors[floor].MidSpin;
            if (targetAudio >= 0.0 && recentEnough && !isMidSpin &&
                !string.Equals(state.Name, "None", StringComparison.OrdinalIgnoreCase))
            {
                _hitSounds.Play(state.Name, state.Volume);
            }

            _nextHitFloor++;
            processed++;
        }

        if (processed == MaxHitSoundsPerFrame)
        {
            double chartTime = PlaybackClock.AudioToChartTime(level, audioSeconds);
            PlaybackPose pose = _timingMap.GetPose(level, chartTime);
            _nextHitFloor = Math.Max(_nextHitFloor, pose.Floor + 1);
        }

        _lastHitAudioSeconds = audioSeconds;
    }

    private double GetHitSoundAudioTime(LevelDocument level, int floor, HitSoundState state)
    {
        double chartTime = _timingMap!.GetEntryTime(floor);
        double audioTime = PlaybackClock.ChartToAudioTime(level, chartTime);
        return audioTime - _hitSounds.GetOffsetSeconds(state.Name);
    }

    private void LoadSynthetic()
    {
        StopPlayback();
        _audio.Unload();
        var sw = Stopwatch.StartNew();
        LevelDocument level = LevelDocument.CreateSynthetic(238_145);
        var index = new SpatialGridIndex(level.Positions);
        sw.Stop();

        _canvas.SetLevel(level, index);
        _timingMap = TimingMapBuilder.Build(level);
        _hitSoundTimeline = HitSoundTimelineBuilder.Build(level);
        _status.Text =
            $"Synthetic | floors {level.FloorCount:N0} | model + spatial index {sw.Elapsed.TotalMilliseconds:N1} ms";
    }

    private void ImportProbeAssets()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select an EditorQoL Asset Probe *-assets folder",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            AssetImportResult result = AssetCache.ImportProbeFolder(dialog.SelectedPath);
            _canvas.ReloadFloorAssets();
            string missing = result.MissingAssets.Count == 0
                ? "complete"
                : "missing " + string.Join(", ", result.MissingAssets);
            _status.Text = $"Imported {result.ImportedCount} floor assets -> {result.CacheDirectory} | {missing}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Asset import failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ImportIconCatalog()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "EditorQoL icon catalog ZIP (*-icon-catalog-assets.zip)|*-icon-catalog-assets.zip|ZIP files (*.zip)|*.zip|All files (*.*)|*.*",
            Title = "Import EditorQoL Icon Catalog"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            IconImportResult result = IconAssetCache.ImportCatalog(dialog.FileName);
            _canvas.ReloadIconAssets();
            _status.Text =
                $"Imported icon catalog: {result.EventIcons} event / {result.FloorIcons} floor / " +
                $"{result.OutlineIcons} outline -> {result.CacheDirectory}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Icon import failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ImportHitSounds()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select an EditorQoL *-hitsounds-assets folder",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            HitSoundImportResult result = HitSoundAssetCache.ImportProbeFolder(dialog.SelectedPath);
            _hitSounds.ReloadAssets();
            _status.Text =
                $"Imported {result.ImportedClips} hit sounds -> {result.CacheDirectory} | " +
                (result.HasManifest ? "timing offsets loaded" : "no timing manifest");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Hit-sound import failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RotateSelected(double delta)
    {
        LevelDocument? level = _canvas.Level;
        int floor = _canvas.SelectedFloor;
        if (level is null || floor <= 0 || floor > level.Angles.Length)
            return;

        StopPlayback();
        int angleIndex = floor - 1;
        level.Angles[angleIndex] = NormalizeAngle(level.Angles[angleIndex] + delta);

        var sw = Stopwatch.StartNew();
        level.RebuildGeometry();
        _canvas.Reindex();
        _timingMap = TimingMapBuilder.Build(level);
        sw.Stop();

        _status.Text =
            $"floor {floor:N0}: angle {level.Angles[angleIndex]:0.###}° | rebuild + index {sw.Elapsed.TotalMilliseconds:N2} ms";
    }

    private void SaveAs()
    {
        LevelDocument? level = _canvas.Level;
        if (level is null || level.SourcePath == "<synthetic>")
            return;

        using var dialog = new SaveFileDialog
        {
            Filter = "ADOFAI levels (*.adofai)|*.adofai|All files (*.*)|*.*",
            FileName = Path.GetFileNameWithoutExtension(level.SourcePath) + ".extreme.adofai",
            Title = "Save edited ADOFAI level"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            var sw = Stopwatch.StartNew();
            AdoFaiSaver.SaveAngles(level, dialog.FileName);
            sw.Stop();
            _status.Text = $"Saved {Path.GetFileName(dialog.FileName)} in {sw.Elapsed.TotalMilliseconds:N1} ms";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static double NormalizeAngle(double angle)
    {
        angle %= 360.0;
        if (angle < 0) angle += 360.0;
        return angle;
    }

    private void BenchmarkViewport()
    {
        if (_canvas.Level is null)
            return;

        const int iterations = 1000;
        var sw = Stopwatch.StartNew();
        long candidates = 0;
        for (int i = 0; i < iterations; i++)
            candidates += _canvas.QueryCurrentViewport();
        sw.Stop();

        _status.Text =
            $"Viewport query x{iterations:N0}: {sw.Elapsed.TotalMilliseconds:N2} ms " +
            $"({sw.Elapsed.TotalMilliseconds / iterations:F4} ms/query), " +
            $"avg candidates {(double)candidates / iterations:N1}";
    }

    private void UpdateRenderStatus() =>
        _renderStatus.Text =
            $"{_canvas.LastRenderMode} / {_canvas.FloorAssetSummary} / {_canvas.IconAssetSummary} / {_hitSounds.AssetSummary} | " +
            $"candidate {_canvas.LastCandidateCount:N0} | drawn {_canvas.LastDrawnCount:N0} | " +
            $"paint {_canvas.LastPaintMilliseconds:F2} ms | selected {_canvas.SelectedFloor}";
}

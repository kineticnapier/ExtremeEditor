using System.Diagnostics;
using ExtremeEditor.Core;

namespace ExtremeEditor.App;

public sealed class MainForm : Form
{
    private readonly LevelCanvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _renderStatus = new();
    private readonly ToolStripButton _open = new("Open");
    private readonly ToolStripButton _synthetic = new("Synthetic 238k");
    private readonly ToolStripButton _frame = new("Frame");
    private readonly ToolStripButton _rotateLeft = new("-15°");
    private readonly ToolStripButton _rotateRight = new("+15°");
    private readonly ToolStripButton _saveAs = new("Save As");
    private readonly ToolStripButton _benchmark = new("Benchmark viewport");

    public MainForm(string? initialFile)
    {
        Text = $"ExtremeEditor {EditorVersion.Current}";
        Width = 1400;
        Height = 850;
        KeyPreview = true;

        var tools = new ToolStrip();
        tools.Items.AddRange([_open, _synthetic, new ToolStripSeparator(), _frame,
            new ToolStripSeparator(), _rotateLeft, _rotateRight, _saveAs,
            new ToolStripSeparator(), _benchmark]);

        var statusStrip = new StatusStrip();
        statusStrip.Items.AddRange([_status, _renderStatus]);

        Controls.Add(_canvas);
        Controls.Add(tools);
        Controls.Add(statusStrip);

        _open.Click += (_, _) => OpenLevel();
        _synthetic.Click += (_, _) => LoadSynthetic();
        _frame.Click += (_, _) => _canvas.FrameAll();
        _rotateLeft.Click += (_, _) => RotateSelected(-15);
        _rotateRight.Click += (_, _) => RotateSelected(15);
        _saveAs.Click += (_, _) => SaveAs();
        _benchmark.Click += (_, _) => BenchmarkViewport();
        _canvas.DiagnosticsChanged += UpdateRenderStatus;

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F) _canvas.FrameAll();
            if (e.KeyCode == Keys.O) OpenLevel();
            if (e.KeyCode == Keys.Escape) _canvas.SelectedFloor = -1;
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
            UseWaitCursor = true;
            _status.Text = "Loading…";
            Application.DoEvents();

            var sw = Stopwatch.StartNew();
            LoadResult loaded = AdoFaiLoader.Load(path);
            var indexWatch = Stopwatch.StartNew();
            var index = new SpatialGridIndex(loaded.Document.Positions);
            indexWatch.Stop();

            _canvas.SetLevel(loaded.Document, index);
            sw.Stop();

            _status.Text =
                $"{Path.GetFileName(path)} | floors {loaded.Document.FloorCount:N0} | " +
                $"actions {loaded.Document.ActionCount:N0} | " +
                $"read {loaded.Metrics.Read.TotalMilliseconds:N1} ms | " +
                $"parse {loaded.Metrics.Parse.TotalMilliseconds:N1} ms | " +
                $"path {loaded.Metrics.BuildPath.TotalMilliseconds:N1} ms | " +
                $"index {indexWatch.Elapsed.TotalMilliseconds:N1} ms | " +
                $"total {sw.Elapsed.TotalMilliseconds:N1} ms";
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

    private void LoadSynthetic()
    {
        var sw = Stopwatch.StartNew();
        LevelDocument level = LevelDocument.CreateSynthetic(238_145);
        var index = new SpatialGridIndex(level.Positions);
        sw.Stop();

        _canvas.SetLevel(level, index);
        _status.Text =
            $"Synthetic | floors {level.FloorCount:N0} | model + spatial index {sw.Elapsed.TotalMilliseconds:N1} ms";
    }

    private void RotateSelected(double delta)
    {
        LevelDocument? level = _canvas.Level;
        int floor = _canvas.SelectedFloor;
        if (level is null || floor <= 0 || floor > level.Angles.Length)
            return;

        int angleIndex = floor - 1;
        level.Angles[angleIndex] = NormalizeAngle(level.Angles[angleIndex] + delta);

        var sw = Stopwatch.StartNew();
        level.RebuildGeometry();
        _canvas.Reindex();
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
            $"candidate {_canvas.LastCandidateCount:N0} | drawn {_canvas.LastDrawnCount:N0} | " +
            $"paint {_canvas.LastPaintMilliseconds:F2} ms | selected {_canvas.SelectedFloor}";
}

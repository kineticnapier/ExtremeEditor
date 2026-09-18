using ExtremeEditor.Audio;
using ExtremeEditor.Core;

namespace ExtremeEditor.App;

public sealed partial class MainForm
{
    private const int VirtualTwirlStartFloor = 748_388;
    private const int VirtualTwirlEndFloor = 4_057_964;

    private readonly ToolStripButton _virtualTwirlCorrection = new("Twirl Pair Fix")
    {
        CheckOnClick = true,
        ToolTipText = "Virtually insert Twirls at floors 748,388 and 4,057,964 for playback only"
    };

    private bool _updatingVirtualTwirlCorrection;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        ToolStrip? tools = Controls.OfType<ToolStrip>().FirstOrDefault();
        if (tools is null || tools.Items.Contains(_virtualTwirlCorrection))
            return;

        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(_virtualTwirlCorrection);
        _virtualTwirlCorrection.CheckedChanged += (_, _) => ApplyVirtualTwirlCorrection();
    }

    private void ApplyVirtualTwirlCorrection()
    {
        if (_updatingVirtualTwirlCorrection)
            return;

        LevelDocument? level = _canvas.Level;
        if (level is null)
        {
            SetVirtualTwirlCorrectionChecked(false);
            return;
        }

        if (_virtualTwirlCorrection.Checked && level.FloorCount <= VirtualTwirlEndFloor)
        {
            SetVirtualTwirlCorrectionChecked(false);
            MessageBox.Show(
                this,
                $"This diagnostic requires at least {VirtualTwirlEndFloor + 1:N0} floors.",
                "Twirl Pair Fix",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        AudioDiagnosticLog? log = AudioDiagnosticLog.Shared;
        StopPlayback();
        UseWaitCursor = true;

        try
        {
            _timingMap = _virtualTwirlCorrection.Checked
                ? TimingMapBuilder.BuildWithVirtualTwirls(
                    level,
                    [VirtualTwirlStartFloor, VirtualTwirlEndFloor])
                : TimingMapBuilder.Build(level);

            if (_hitSoundTimeline is not null)
                _audio.ConfigureHitSounds(level, _timingMap, _hitSoundTimeline);

            string mode = _virtualTwirlCorrection.Checked ? "ON" : "OFF";
            _virtualTwirlCorrection.Text = _virtualTwirlCorrection.Checked
                ? "Twirl Pair Fix: ON"
                : "Twirl Pair Fix";
            _status.Text =
                $"Virtual Twirl pair {mode} | floors {VirtualTwirlStartFloor:N0}, {VirtualTwirlEndFloor:N0} | " +
                $"chart duration {_timingMap.Duration:F9} s | source file unchanged";

            log?.Write(
                "main_form.virtual_twirl_pair",
                $"enabled={_virtualTwirlCorrection.Checked} " +
                $"start_floor={VirtualTwirlStartFloor} end_floor={VirtualTwirlEndFloor} " +
                $"duration_s={_timingMap.Duration:F9}");
        }
        catch (Exception ex)
        {
            SetVirtualTwirlCorrectionChecked(false);
            log?.Write(
                "main_form.virtual_twirl_pair_failed",
                $"exception={ex.GetType().FullName} message={ex.Message}");
            MessageBox.Show(
                this,
                ex.ToString(),
                "Twirl Pair Fix failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void SetVirtualTwirlCorrectionChecked(bool value)
    {
        _updatingVirtualTwirlCorrection = true;
        try
        {
            _virtualTwirlCorrection.Checked = value;
            _virtualTwirlCorrection.Text = value ? "Twirl Pair Fix: ON" : "Twirl Pair Fix";
        }
        finally
        {
            _updatingVirtualTwirlCorrection = false;
        }
    }
}

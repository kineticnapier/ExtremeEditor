using System.Diagnostics;
using System.Text;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;

namespace ExtremeEditor.App;

public sealed partial class MainForm
{
    private readonly ToolStripButton _timingProbe = new("Timing Probe");

    private void InitializeTimingProbeUi(ToolStrip tools)
    {
        tools.Items.Add(_timingProbe);
        _timingProbe.Click += async (_, _) => await RunTimingProbeAsync();
    }

    private async Task RunTimingProbeAsync()
    {
        LevelDocument? level = _canvas.Level;
        TimingMap? timing = _timingMap;
        if (level is null || timing is null)
            return;

        AudioDiagnosticLog? log = AudioDiagnosticLog.Shared;
        _timingProbe.Enabled = false;
        UseWaitCursor = true;
        _status.Text = "Running timing probe…";

        try
        {
            var watch = Stopwatch.StartNew();
            TimingProbeResult result = await Task.Run(() => TimingProbe.Analyze(level, timing));
            watch.Stop();

            string first = result.FirstMismatchFloor?.ToString("N0") ?? "none";
            log?.Write("timing_probe.summary",
                $"first_mismatch_floor={first} mismatch_count={result.MismatchCount} " +
                $"current_duration_s={result.CurrentDurationSeconds:F9} " +
                $"reference_duration_s={result.ReferenceDurationSeconds:F9} " +
                $"max_delta_s={result.MaxCumulativeDeltaSeconds:F9} elapsed_ms={watch.Elapsed.TotalMilliseconds:F3}");

            foreach (TimingProbeRow row in result.Rows)
            {
                log?.Write("timing_probe.row",
                    $"floor={row.Floor} current_entry_s={row.CurrentEntrySeconds:F9} " +
                    $"reference_entry_s={row.ReferenceEntrySeconds:F9} " +
                    $"current_floor_s={row.CurrentFloorSeconds:F9} " +
                    $"reference_floor_s={row.ReferenceFloorSeconds:F9} " +
                    $"current_angle_deg={row.CurrentAngleDegrees:F6} " +
                    $"reference_angle_deg={row.ReferenceAngleDegrees:F6} " +
                    $"current_bpm={row.CurrentBpm:F6} reference_start_bpm={row.ReferenceStartBpm:F6} " +
                    $"reference_end_bpm={row.ReferenceEndBpm:F6} ccw={row.IsCcw} " +
                    $"planets={row.NumPlanets} set_speed_offsets={row.SetSpeedOffsets}");
            }

            TimingProbeRow? firstRow = result.FirstMismatchFloor is int mismatchFloor
                ? result.Rows.FirstOrDefault(row => row.Floor == mismatchFloor)
                : null;
            string firstDetails = firstRow is null
                ? string.Empty
                : $" | angle {firstRow.CurrentAngleDegrees:F3}°/{firstRow.ReferenceAngleDegrees:F3}°" +
                  $" | BPM {firstRow.CurrentBpm:F3}/{firstRow.ReferenceStartBpm:F3}->{firstRow.ReferenceEndBpm:F3}" +
                  $" | offsets {firstRow.SetSpeedOffsets}";

            _status.Text =
                $"Timing probe | first {first} | mismatches {result.MismatchCount:N0} | " +
                $"duration {result.CurrentDurationSeconds:F6}s/{result.ReferenceDurationSeconds:F6}s | " +
                $"max Δ {result.MaxCumulativeDeltaSeconds:F6}s | {watch.Elapsed.TotalMilliseconds:N1} ms" +
                firstDetails;

            MessageBox.Show(
                this,
                BuildTimingProbeReport(result, watch.Elapsed),
                "Timing Probe",
                MessageBoxButtons.OK,
                result.FirstMismatchFloor is null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            log?.Write("timing_probe.failed",
                $"exception={ex.GetType().FullName} message={ex.Message}");
            MessageBox.Show(this, ex.ToString(), "Timing probe failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _status.Text = "Timing probe failed";
        }
        finally
        {
            UseWaitCursor = false;
            _timingProbe.Enabled = true;
        }
    }

    private static string BuildTimingProbeReport(TimingProbeResult result, TimeSpan elapsed)
    {
        var text = new StringBuilder();
        text.AppendLine($"First mismatch: {result.FirstMismatchFloor?.ToString("N0") ?? "none"}");
        text.AppendLine($"Mismatching floors: {result.MismatchCount:N0}");
        text.AppendLine($"Current duration: {result.CurrentDurationSeconds:F9} s");
        text.AppendLine($"Reference duration: {result.ReferenceDurationSeconds:F9} s");
        text.AppendLine($"Max cumulative delta: {result.MaxCumulativeDeltaSeconds:F9} s");
        text.AppendLine($"Probe time: {elapsed.TotalMilliseconds:N1} ms");

        if (result.Rows.Count == 0)
            return text.ToString();

        text.AppendLine();
        text.AppendLine("Context rows:");
        foreach (TimingProbeRow row in result.Rows)
        {
            text.AppendLine(
                $"floor {row.Floor:N0}: " +
                $"entry {row.CurrentEntrySeconds:F9}/{row.ReferenceEntrySeconds:F9}, " +
                $"dt {row.CurrentFloorSeconds:F9}/{row.ReferenceFloorSeconds:F9}, " +
                $"angle {row.CurrentAngleDegrees:F3}°/{row.ReferenceAngleDegrees:F3}°, " +
                $"BPM {row.CurrentBpm:F3}/{row.ReferenceStartBpm:F3}->{row.ReferenceEndBpm:F3}, " +
                $"ccw={row.IsCcw}, planets={row.NumPlanets}, offsets={row.SetSpeedOffsets}");
        }

        return text.ToString();
    }
}

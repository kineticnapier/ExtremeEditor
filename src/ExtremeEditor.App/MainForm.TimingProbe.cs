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
            double? audioDurationSeconds = _audio.IsLoaded
                ? _audio.Duration.TotalSeconds
                : null;
            double? missingTwirlTargetSeconds = audioDurationSeconds is double audioSeconds && audioSeconds > 0.0
                ? PlaybackClock.AudioToChartTime(level, audioSeconds)
                : null;

            var watch = Stopwatch.StartNew();
            (TimingProbeResult result,
                ArcExcessProbeResult arc,
                StockTimingProbeResult stock,
                MissingTwirlProbeResult? missingTwirl) = await Task.Run(() =>
            {
                TimingProbeResult timingResult = TimingProbe.Analyze(level, timing);
                ArcExcessProbeResult arcResult = TimingProbe.AnalyzeArcExcess(level, timing);
                StockTimingProbeResult stockResult = StockTimingProbe.Analyze(level, timing);
                MissingTwirlProbeResult? missingTwirlResult = missingTwirlTargetSeconds is double target
                    ? MissingTwirlProbe.Analyze(level, timing, target, 12)
                    : null;
                return (timingResult, arcResult, stockResult, missingTwirlResult);
            });
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

            log?.Write("timing_probe.stock_summary",
                $"first_mismatch_floor={stock.FirstMismatchFloor?.ToString() ?? "none"} " +
                $"mismatch_count={stock.MismatchCount} current_duration_s={stock.CurrentDurationSeconds:F9} " +
                $"stock_duration_s={stock.StockDurationSeconds:F9} " +
                $"duration_delta_s={stock.CurrentDurationSeconds - stock.StockDurationSeconds:F9} " +
                $"max_delta_s={stock.MaxCumulativeDeltaSeconds:F9}");
            foreach (StockTimingProbeRow row in stock.Rows)
            {
                log?.Write("timing_probe.stock_row",
                    $"floor={row.Floor} current_entry_s={row.CurrentEntrySeconds:F9} " +
                    $"stock_entry_s={row.StockEntrySeconds:F9} " +
                    $"current_floor_s={row.CurrentFloorSeconds:F9} stock_floor_s={row.StockFloorSeconds:F9} " +
                    $"current_angle_deg={row.CurrentAngleDegrees:F6} stock_angle_deg={row.StockAngleDegrees:F6} " +
                    $"current_bpm={row.CurrentBpm:F6} stock_bpm={row.StockBpm:F6} " +
                    $"ccw={row.IsCcw} planets={row.NumPlanets} set_speed_offsets={row.SetSpeedOffsets}");
            }

            if (missingTwirl is not null)
            {
                log?.Write("timing_probe.missing_twirl_summary",
                    $"audio_duration_s={audioDurationSeconds:F9} " +
                    $"target_chart_s={missingTwirl.TargetDurationSeconds:F9} " +
                    $"current_duration_s={missingTwirl.CurrentDurationSeconds:F9} " +
                    $"best_floor={missingTwirl.BestFloor} " +
                    $"best_duration_s={missingTwirl.BestDurationSeconds:F9} " +
                    $"best_error_s={missingTwirl.BestAbsoluteErrorSeconds:F9}");
                foreach (MissingTwirlCandidate candidate in missingTwirl.Candidates)
                {
                    log?.Write("timing_probe.missing_twirl_candidate",
                        $"floor={candidate.Floor} duration_s={candidate.DurationSeconds:F9} " +
                        $"error_s={candidate.AbsoluteErrorSeconds:F9}");
                }
            }

            log?.Write("timing_probe.arc_summary",
                $"long_arc_floors={arc.LongArcFloorCount} current_s={arc.CurrentSeconds:F9} " +
                $"complementary_s={arc.ComplementarySeconds:F9} excess_s={arc.TotalExcessSeconds:F9} " +
                $"intervals={arc.Intervals.Count}");
            foreach (ArcExcessInterval interval in arc.Intervals)
            {
                log?.Write("timing_probe.arc_interval",
                    $"start_floor={interval.StartFloor} end_floor={interval.EndFloor} bpm={interval.Bpm:F6} " +
                    $"long_arc_floors={interval.LongArcFloorCount} current_s={interval.CurrentSeconds:F9} " +
                    $"complementary_s={interval.ComplementarySeconds:F9} excess_s={interval.ExcessSeconds:F9} " +
                    $"cumulative_excess_s={interval.CumulativeExcessSeconds:F9}");
            }

            TimingProbeRow? firstRow = result.FirstMismatchFloor is int mismatchFloor
                ? result.Rows.FirstOrDefault(row => row.Floor == mismatchFloor)
                : null;
            string firstDetails = firstRow is null
                ? string.Empty
                : $" | angle {firstRow.CurrentAngleDegrees:F3}°/{firstRow.ReferenceAngleDegrees:F3}°" +
                  $" | BPM {firstRow.CurrentBpm:F3}/{firstRow.ReferenceStartBpm:F3}->{firstRow.ReferenceEndBpm:F3}" +
                  $" | offsets {firstRow.SetSpeedOffsets}";

            double stockDurationDelta = stock.CurrentDurationSeconds - stock.StockDurationSeconds;
            string missingTwirlStatus = missingTwirl is null
                ? string.Empty
                : $" | missing-twirl {missingTwirl.BestFloor:N0} -> {missingTwirl.BestDurationSeconds:F6}s " +
                  $"(err {missingTwirl.BestAbsoluteErrorSeconds:F6}s)";
            _status.Text =
                $"Timing probe | stock {stock.StockDurationSeconds:F6}s | map-stock {stockDurationDelta:+0.000000;-0.000000;0.000000}s | " +
                $"stock first {stock.FirstMismatchFloor?.ToString("N0") ?? "none"} | " +
                $"arc excess {arc.TotalExcessSeconds:F6}s{missingTwirlStatus} | {watch.Elapsed.TotalMilliseconds:N1} ms" +
                firstDetails;

            MessageBox.Show(
                this,
                BuildTimingProbeReport(result, arc, stock, missingTwirl, audioDurationSeconds, watch.Elapsed),
                "Timing Probe",
                MessageBoxButtons.OK,
                result.FirstMismatchFloor is null && stock.FirstMismatchFloor is null && arc.TotalExcessSeconds <= 0.000001
                    ? MessageBoxIcon.Information
                    : MessageBoxIcon.Warning);
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

    private static string BuildTimingProbeReport(
        TimingProbeResult result,
        ArcExcessProbeResult arc,
        StockTimingProbeResult stock,
        MissingTwirlProbeResult? missingTwirl,
        double? audioDurationSeconds,
        TimeSpan elapsed)
    {
        var text = new StringBuilder();
        text.AppendLine($"First mismatch: {result.FirstMismatchFloor?.ToString("N0") ?? "none"}");
        text.AppendLine($"Mismatching floors: {result.MismatchCount:N0}");
        text.AppendLine($"Current duration: {result.CurrentDurationSeconds:F9} s");
        text.AppendLine($"Reference duration: {result.ReferenceDurationSeconds:F9} s");
        text.AppendLine($"Max cumulative delta: {result.MaxCumulativeDeltaSeconds:F9} s");
        text.AppendLine($"Probe time: {elapsed.TotalMilliseconds:N1} ms");

        text.AppendLine();
        text.AppendLine("Stock DLL-path probe:");
        text.AppendLine($"First mismatch: {stock.FirstMismatchFloor?.ToString("N0") ?? "none"}");
        text.AppendLine($"Mismatching floors: {stock.MismatchCount:N0}");
        text.AppendLine($"TimingMap duration: {stock.CurrentDurationSeconds:F9} s");
        text.AppendLine($"Stock duration: {stock.StockDurationSeconds:F9} s");
        text.AppendLine($"TimingMap - stock: {stock.CurrentDurationSeconds - stock.StockDurationSeconds:+0.000000000;-0.000000000;0.000000000} s");
        text.AppendLine($"Max cumulative delta: {stock.MaxCumulativeDeltaSeconds:F9} s");

        if (stock.Rows.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Stock first-mismatch context rows:");
            foreach (StockTimingProbeRow row in stock.Rows)
            {
                text.AppendLine(
                    $"floor {row.Floor:N0}: " +
                    $"entry {row.CurrentEntrySeconds:F9}/{row.StockEntrySeconds:F9}, " +
                    $"dt {row.CurrentFloorSeconds:F9}/{row.StockFloorSeconds:F9}, " +
                    $"angle {row.CurrentAngleDegrees:F3}°/{row.StockAngleDegrees:F3}°, " +
                    $"BPM {row.CurrentBpm:F3}/{row.StockBpm:F3}, " +
                    $"ccw={row.IsCcw}, planets={row.NumPlanets}, offsets={row.SetSpeedOffsets}");
            }
        }

        text.AppendLine();
        text.AppendLine("Single missing-Twirl hypothesis:");
        if (missingTwirl is null)
        {
            text.AppendLine("Not run: audio is unavailable.");
        }
        else
        {
            if (audioDurationSeconds is double audioSeconds)
                text.AppendLine($"Audio duration: {audioSeconds:F9} s");
            text.AppendLine($"Target chart time at audio end: {missingTwirl.TargetDurationSeconds:F9} s");
            text.AppendLine($"Current duration: {missingTwirl.CurrentDurationSeconds:F9} s");
            text.AppendLine($"Best virtual Twirl floor: {missingTwirl.BestFloor:N0}");
            text.AppendLine($"Duration after virtual Twirl: {missingTwirl.BestDurationSeconds:F9} s");
            text.AppendLine($"Duration change: {missingTwirl.BestDurationSeconds - missingTwirl.CurrentDurationSeconds:+0.000000000;-0.000000000;0.000000000} s");
            text.AppendLine($"Absolute target error: {missingTwirl.BestAbsoluteErrorSeconds:F9} s");

            if (missingTwirl.Candidates.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("Top missing-Twirl candidates:");
                foreach (MissingTwirlCandidate candidate in missingTwirl.Candidates)
                {
                    text.AppendLine(
                        $"floor {candidate.Floor:N0}: duration {candidate.DurationSeconds:F9} s, " +
                        $"error {candidate.AbsoluteErrorSeconds:F9} s");
                }
            }
        }

        text.AppendLine();
        text.AppendLine("Arc-excess hypothesis:");
        text.AppendLine($"Long-arc floors (>180°): {arc.LongArcFloorCount:N0}");
        text.AppendLine($"Current summed duration: {arc.CurrentSeconds:F9} s");
        text.AppendLine($"Complementary-arc duration: {arc.ComplementarySeconds:F9} s");
        text.AppendLine($"Total excess: {arc.TotalExcessSeconds:F9} s");

        ArcExcessInterval[] topIntervals = arc.Intervals
            .Where(interval => interval.ExcessSeconds > 0.000000001)
            .OrderByDescending(interval => interval.ExcessSeconds)
            .Take(12)
            .ToArray();
        if (topIntervals.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("Top SetSpeed ranges by excess:");
            foreach (ArcExcessInterval interval in topIntervals)
            {
                text.AppendLine(
                    $"{interval.StartFloor:N0}-{interval.EndFloor:N0}: " +
                    $"BPM {interval.Bpm:F3}, long {interval.LongArcFloorCount:N0}, " +
                    $"current {interval.CurrentSeconds:F6}s, comp {interval.ComplementarySeconds:F6}s, " +
                    $"excess {interval.ExcessSeconds:F6}s, cumulative {interval.CumulativeExcessSeconds:F6}s");
            }
        }

        if (result.Rows.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Reference first-mismatch context rows:");
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
        }

        return text.ToString();
    }
}

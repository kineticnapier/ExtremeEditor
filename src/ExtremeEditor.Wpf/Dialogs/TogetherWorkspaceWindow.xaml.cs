using System.IO;
using System.Windows;
using System.Windows.Controls;
using ExtremeEditor.Core;
using Microsoft.Win32;

namespace ExtremeEditor.Wpf;

/// <summary>
/// Explicit local-Git Together controls. Chart editing continues in the
/// existing MainWindow/EditorSession; the panel never implements a second
/// independent angle/event editor or starts network synchronization.
/// </summary>
public partial class TogetherWorkspaceWindow : Window
{
    private readonly MainWindow _host;
    private readonly TogetherWorkspaceController _controller = new();
    private bool _refreshing;

    internal TogetherWorkspaceWindow(MainWindow host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Owner = host;
        InitializeComponent();
        RefreshStatus();
    }

    private void CreateWorkspaceClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            LevelDocument source = ReadSourceTemplate();
            string workspacePath = WorkspacePathBox.Text.Trim();
            string owner = OwnerIdBox.Text.Trim();
            if (!int.TryParse(SampleRateBox.Text.Trim(), out int sampleRate) || sampleRate <= 0)
                throw new ArgumentException("Enter a positive decoded audio sample rate.");

            int[] cuts = ParseCuts(CutFloorsBox.Text);
            LevelSegment[] sections = LevelSegmentPartitioner.Split(source, cuts);
            string[] owners = OwnersBox.Text.Split(',', StringSplitOptions.TrimEntries);
            if (owners.Length != sections.Length ||
                owners.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException(
                    "Enter one owner ID for each Segment region, separated by commas.");

            TimingMap timing = TimingMapBuilder.Build(source);
            var content = new Dictionary<string, LevelSegment>(StringComparer.Ordinal);
            var assignments = new List<SegmentTimePartition>();
            for (int i = 0; i < sections.Length; i++)
            {
                LevelSegment segment = sections[i];
                string id = $"segment-{i + 1}";
                long first = ToSample(
                    PlaybackClock.ChartToAudioTime(
                        source, timing.GetEntryTime(segment.StartFloor)), sampleRate);
                long last = ToSample(
                    PlaybackClock.ChartToAudioTime(
                        source, segment.EndFloorExclusive == source.FloorCount
                            ? timing.Duration
                            : timing.GetEntryTime(segment.EndFloorExclusive)), sampleRate);
                content.Add(id, segment);
                assignments.Add(new SegmentTimePartition(
                    id, first, last, $"{id}-initial", first, owners[i],
                    SegmentPartitionStatus.Active));
            }

            _controller.Create(workspacePath, source,
                new SegmentGitSnapshot(
                    new SegmentTimeTimeline(sampleRate, assignments),
                    content), owner);
            LoadIntoEditor();
        });

    private void OpenWorkspaceClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            _controller.Open(
                WorkspacePathBox.Text.Trim(), ReadSourceTemplate(),
                OwnerIdBox.Text.Trim());
            LoadIntoEditor();
        });

    private void ReloadViewClick(object sender, RoutedEventArgs e) =>
        Safely(LoadIntoEditor);

    private void SegmentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || SegmentList.SelectedItem is not TogetherSegmentDisplay item)
            return;
        if (!item.CanEdit)
        {
            TogetherStatusText.Text =
                $"Segment {item.SegmentId} belongs to {item.OwnerId ?? "nobody"} " +
                "or is parked. It is read-only.";
            return;
        }
        Safely(() =>
        {
            _controller.SelectSegment(item.SegmentId);
            TogetherStatusText.Text =
                $"Selected {item.SegmentId} floors [{item.StartFloor},{item.EndFloorExclusive}). " +
                "Edit those floors in the main editor, then Stage selected Segment.";
        });
    }

    private void SaveSegmentClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            EditorSession editor = _host.TogetherCurrentEditor ??
                throw new InvalidOperationException("Open a Together chart in the main editor first.");
            _controller.StageSelected(editor);
        });

    private void CommitClick(object sender, RoutedEventArgs e) =>
        Safely(() => _controller.Commit(CommitMessageBox.Text.Trim()));

    private void ConnectClick(object sender, RoutedEventArgs e) =>
        Safely(() => _controller.ConnectLocalBare(BareRemoteBox.Text.Trim()));

    private void FetchClick(object sender, RoutedEventArgs e) =>
        Safely(() => _controller.Fetch());

    private void PullClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            SegmentGitPullResult result =
                _controller.Pull(_host.TogetherCurrentEditor);
            if (result.Success)
                LoadIntoEditor();
            else
                TogetherStatusText.Text =
                    "Merge conflict. Content: " +
                    string.Join(", ", result.ContentConflictSegmentIds) +
                    "; timeline: " +
                    string.Join(", ", result.TimelineConflicts.Select(c => c.Kind.ToString())) +
                    ". Your local edits remain untouched.";
        });

    private void PushClick(object sender, RoutedEventArgs e) =>
        Safely(() => _controller.Push());

    private void ExportClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export published Together revision",
                Filter = "ADOFAI chart (*.adofai)|*.adofai",
                DefaultExt = ".adofai",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = "Together-merged.adofai"
            };
            if (dialog.ShowDialog(this) != true)
                return;
            bool overwrite = File.Exists(dialog.FileName);
            SegmentGitAdoFaiPublication publication =
                _controller.Export(dialog.FileName, overwriteExisting: overwrite);
            TogetherStatusText.Text =
                $"Published {publication.FloorCount} floors from commit " +
                $"{publication.SourceCommit[..Math.Min(12, publication.SourceCommit.Length)]} " +
                $"to {publication.DestinationPath}";
        });

    private void LoadIntoEditor()
    {
        LevelDocument chart = _controller.GetEditorDocument();
        _host.ApplyTogetherDocument(chart);
        RefreshStatus();
    }

    private LevelDocument ReadSourceTemplate()
    {
        LevelDocument current = _host.TogetherCurrentLevel ??
            throw new InvalidOperationException(
                "Open an existing .adofai chart in the main editor before creating Together.");
        if (current.SourcePath == "<synthetic>" || !File.Exists(current.SourcePath))
            throw new InvalidOperationException(
                "Together requires a real .adofai source file opened in ExtremeEditor.");
        return AdoFaiLoader.Load(current.SourcePath).Document;
    }

    private void RefreshStatus()
    {
        TogetherWorkspaceDisplay status = _controller.Display;
        _refreshing = true;
        try
        {
            SegmentList.ItemsSource = status.Segments;
            if (status.SelectedSegmentId is string id)
                SegmentList.SelectedItem =
                    status.Segments.FirstOrDefault(s => s.SegmentId == id);
        }
        finally
        {
            _refreshing = false;
        }

        bool connected = status.WorkspaceDirectory is not null;
        SaveSegmentButton.IsEnabled = connected &&
            status.SelectedSegmentId is not null && !status.HasStagedChanges;
        CommitButton.IsEnabled = connected && status.HasStagedChanges;
        FetchButton.IsEnabled = connected;
        PullButton.IsEnabled = connected && !status.HasStagedChanges;
        PushButton.IsEnabled = connected && !status.HasStagedChanges;
        ExportButton.IsEnabled = connected && !status.HasStagedChanges;

        if (!connected)
        {
            TogetherStatusText.Text =
                "Open an .adofai chart first; create or open an isolated local Together Git workspace.";
            return;
        }

        string conflict = status.ContentConflictSegmentIds.Count == 0 &&
                          status.TimelineConflicts.Count == 0
            ? string.Empty
            : " | Conflicts: " +
              string.Join(",", status.ContentConflictSegmentIds) +
              " " + string.Join(",", status.TimelineConflicts.Select(c => c.Kind));
        TogetherStatusText.Text =
            $"Workspace: {status.WorkspaceDirectory} | Commit: " +
            $"{status.CurrentCommit?[..Math.Min(12, status.CurrentCommit.Length)]} | " +
            $"{(status.HasStagedChanges ? "STAGED — commit required" : "no pending stage")}" +
            conflict;
    }

    private void Safely(Action action)
    {
        try
        {
            action();
            RefreshStatus();
        }
        catch (OperationCanceledException)
        {
            TogetherStatusText.Text = "Operation cancelled. Your edits have not been discarded.";
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or ArgumentException or
            InvalidDataException or UnauthorizedAccessException)
        {
            TogetherStatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message,
                "ExtremeEditor Together", MessageBoxButton.OK, MessageBoxImage.Warning);
            // Do not clear conflicts or user-authored changes on failure.
        }
    }

    private static int[] ParseCuts(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<int>();
        return text.Split(',', StringSplitOptions.TrimEntries).Select(value =>
            int.TryParse(value, out int number)
                ? number
                : throw new ArgumentException("Cut floors must be comma-separated integers."))
            .ToArray();
    }

    private static long ToSample(double seconds, int sampleRate) =>
        checked((long)Math.Round(seconds * sampleRate,
            MidpointRounding.AwayFromZero));
}

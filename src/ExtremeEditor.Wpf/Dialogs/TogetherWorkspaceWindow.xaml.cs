using System.IO;
using System.Windows;
using System.Windows.Controls;
using ExtremeEditor.Core;
using Microsoft.Win32;

namespace ExtremeEditor.Wpf;

/// <summary>
/// Japanese-first, local-Git Together UI. Every synchronizing operation is
/// explicit. All chart edits still occur in MainWindow's EditorSession.
/// This PoC has NO invite URLs, cloud service or realtime remote transport.
/// </summary>
public partial class TogetherWorkspaceWindow : Window
{
    private readonly MainWindow _host;
    private readonly TogetherWorkspaceController _controller = new();
    private bool _refreshing;
    private string? _notice;

    internal TogetherWorkspaceWindow(MainWindow host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Owner = host;
        InitializeComponent();
        RefreshStatus();
    }

    private void ShowCreateProjectClick(object sender, RoutedEventArgs e) =>
        ShowSetup(forCreation: true);

    private void ShowJoinProjectClick(object sender, RoutedEventArgs e) =>
        ShowSetup(forCreation: false);

    private void ShowSetup(bool forCreation)
    {
        SetupPanel.Visibility = Visibility.Visible;
        SetupPanel.Header = forCreation ? "共同編集を新しく作成" : "既存の共同編集に参加";
        CreateWorkspaceButton.Visibility =
            forCreation ? Visibility.Visible : Visibility.Collapsed;
        OpenWorkspaceButton.Visibility =
            forCreation ? Visibility.Collapsed : Visibility.Visible;
        _notice = forCreation
            ? "新しい作業フォルダーを指定してください。作成前に「詳細設定」で音声のサンプル周波数を確認してください。"
            : "既にコピーされた共同編集の作業フォルダーを指定してください。招待リンクからの参加には未対応です。";
        RefreshStatus();
        WorkspacePathBox.Focus();
    }

    private void CreateWorkspaceClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            LevelDocument source = ReadSourceTemplate();
            string workspacePath = RequiredPath(WorkspacePathBox.Text, "作業フォルダー");
            string owner = RequiredOwner();
            if (ConfirmSampleRateBox.IsChecked != true)
            {
                AdvancedControlsExpander.IsExpanded = true;
                throw new ArgumentException(
                    "共同編集を作成する前に、「詳細設定」で音声ファイルのサンプル周波数を確認し、確認欄にチェックしてください。48,000 Hzは初期値で、自動測定値ではありません。");
            }
            if (!int.TryParse(SampleRateBox.Text.Trim(), out int sampleRate) ||
                sampleRate <= 0)
                throw new ArgumentException(
                    "詳細設定に正しい音声サンプル周波数（Hz）を入力してください。");

            int[] cuts = ParseCuts(CutFloorsBox.Text);
            if (cuts.Length == 0 && source.FloorCount > 1)
                cuts = [Math.Clamp(source.FloorCount / 2, 1, source.FloorCount - 1)];

            LevelSegment[] sections =
                LevelSegmentPartitioner.Split(source, cuts).ToArray();

            string[] owners = ParseOwners(OwnersBox.Text, sections.Length, owner);
            if (owners.Length != sections.Length)
                throw new ArgumentException(
                    $"担当者の数が区間数と一致しません。{sections.Length}人分の識別名をカンマ区切りで指定してください。");

            TimingMap timing = TimingMapBuilder.Build(source);
            var segments = new Dictionary<string, LevelSegment>(StringComparer.Ordinal);
            var assignments = new List<SegmentTimePartition>();
            for (int i = 0; i < sections.Length; i++)
            {
                LevelSegment segment = sections[i];
                string id = $"segment-{i + 1}";
                long start = ToSample(
                    PlaybackClock.ChartToAudioTime(
                        source, timing.GetEntryTime(segment.StartFloor)), sampleRate);
                long end = ToSample(
                    PlaybackClock.ChartToAudioTime(
                        source, segment.EndFloorExclusive == source.FloorCount
                            ? timing.Duration
                            : timing.GetEntryTime(segment.EndFloorExclusive)), sampleRate);

                segments.Add(id, segment);
                assignments.Add(new SegmentTimePartition(
                    id, start, end, $"{id}-initial", start,
                    owners[i], SegmentPartitionStatus.Active));
            }

            _controller.Create(workspacePath, source,
                new SegmentGitSnapshot(
                    new SegmentTimeTimeline(sampleRate, assignments),
                    segments), owner);
            LoadIntoEditor();
            SetupPanel.Visibility = Visibility.Collapsed;
            _notice = "共同編集を作成しました。自分の担当区間を選び、メイン画面で編集してください。共有先の設定は「詳細設定」から行えます。";
        });

    private void OpenWorkspaceClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            _controller.Open(
                RequiredPath(WorkspacePathBox.Text, "作業フォルダー"),
                ReadSourceTemplate(), RequiredOwner());
            LoadIntoEditor();
            SetupPanel.Visibility = Visibility.Collapsed;
            _notice = "共同編集に参加しました。自分の担当区間を選んで編集してください。";
        });

    private void ReloadViewClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            LoadIntoEditor();
            _notice = "Gitの記録内容から譜面を再表示しました。";
        });

    private void SegmentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || SegmentList.SelectedItem is not TogetherSegmentDisplay segment)
            return;

        if (!segment.CanEdit)
        {
            _notice = $"「{segment.SegmentId}」は他の担当者の区間、または保留中の区間です。編集内容は共有できません。";
            RefreshStatus();
            return;
        }

        Safely(() =>
        {
            _controller.SelectSegment(segment.SegmentId);
            _notice =
                $"「{segment.SegmentId}」（床 {segment.FloorRangeLabel}）を選択しました。メイン画面で編集後、「変更を共有」を押してください。";
        });
    }

    // Expert-mode controls remain explicit; ordinary Save/Save As is not Git.
    private void SaveSegmentClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            EditorSession editor = RequiredEditor();
            _controller.StageSelected(editor);
            _notice = "選択中の担当区間を保存準備しました。共有するには履歴に記録して送信してください。";
        });

    private void CommitClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            string revision = _controller.Commit(CommitMessageBox.Text.Trim());
            _notice = $"変更をローカル履歴に記録しました（{ShortSha(revision)}）。共有先への送信はまだです。";
        });

    private void ConnectClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            _controller.ConnectLocalBare(RequiredPath(
                BareRemoteBox.Text, "Git共有フォルダー"));
            _notice = "ローカルGit共有フォルダーに接続しました。変更はまだ送信していません。";
        });

    private void FetchClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            string remote = _controller.Fetch();
            _notice = $"共有先の履歴を取得しました（{ShortSha(remote)}）。メイン画面の譜面は変更していません。";
        });

    private void PullClick(object sender, RoutedEventArgs e) =>
        Safely(ReceiveChanges);

    private void ReceiveChangesClick(object sender, RoutedEventArgs e) =>
        Safely(ReceiveChanges);

    private void ReceiveChanges()
    {
        SegmentGitPullResult result = _controller.Pull(_host.TogetherCurrentEditor);
        if (result.Success)
        {
            LoadIntoEditor();
            _notice = "共有先の変更を取り込みました。現在の譜面に反映済みです。";
        }
        else
        {
            _notice = "変更の競合が発生しました。ローカル編集は保持しています。詳細設定で担当区間と履歴を確認してください。" +
                $" 競合区間: {string.Join("、", result.ContentConflictSegmentIds)}";
        }
    }

    private void PushClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            string published = _controller.Push();
            _notice = $"ローカルGit共有フォルダーへ送信しました（{ShortSha(published)}）。";
        });

    /// <summary>
    /// Explicit user-initiated Stage -> Commit -> Push, never automatic Pull,
    /// force push or rollback. A failed Push leaves the local commit intact.
    /// </summary>
    private void ShareChangesClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            TogetherWorkspaceDisplay current = _controller.Display;
            if (current.WorkspaceDirectory is null)
                throw new InvalidOperationException("先に共同編集を作成または参加してください。");

            EditorSession editor = RequiredEditor();
            if (editor.IsDirty && !current.HasStagedChanges)
            {
                if (current.SelectedSegmentId is null)
                    throw new InvalidOperationException(
                        "変更を共有する前に、自分の担当区間を一覧から選択してください。");
                _controller.StageSelected(editor);
            }
            else if (editor.IsDirty && current.HasStagedChanges)
            {
                throw new InvalidOperationException(
                    "前回の保存準備後に未共有の編集が残っています。まず「詳細設定」で履歴を記録し、差分を確認してください。");
            }

            if (_controller.Display.HasStagedChanges)
            {
                string revision = _controller.Commit(
                    string.IsNullOrWhiteSpace(CommitMessageBox.Text)
                        ? "担当区間を編集" : CommitMessageBox.Text.Trim());
                _notice = $"ローカル履歴に記録しました（{ShortSha(revision)}）。続けて共有先へ送信します。";
            }

            // A missing or outdated remote will fail here. Never rewrite
            // commits or discard editor state when publication is rejected.
            string published = _controller.Push();
            _notice = $"変更を共有しました（{ShortSha(published)}）。";
        });

    private void ExportClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            if (RequiredEditor().IsDirty)
                throw new InvalidOperationException(
                    "メイン画面に未共有の編集があります。担当区間の変更を共有するか、編集内容を保存してから書き出してください。");
            var dialog = new SaveFileDialog
            {
                Title = "共同編集から完成譜面を書き出す",
                Filter = "ADOFAI 譜面 (*.adofai)|*.adofai",
                DefaultExt = ".adofai",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = "Together-完成譜面.adofai"
            };
            if (dialog.ShowDialog(this) != true)
                return;
            bool overwrite = File.Exists(dialog.FileName);
            SegmentGitAdoFaiPublication published =
                _controller.Export(dialog.FileName, overwriteExisting: overwrite);
            _notice = $"完成譜面を書き出しました（{published.FloorCount}床、履歴 {ShortSha(published.SourceCommit)}）。保存先: {published.DestinationPath}";
        });

    private void LoadIntoEditor()
    {
        LevelDocument chart = _controller.GetEditorDocument();
        _host.ApplyTogetherDocument(chart);
        RefreshStatus();
    }

    private EditorSession RequiredEditor() =>
        _host.TogetherCurrentEditor ??
        throw new InvalidOperationException(
            "メイン画面で共同編集の譜面を開いてください。");

    private LevelDocument ReadSourceTemplate()
    {
        LevelDocument current = _host.TogetherCurrentLevel ??
            throw new InvalidOperationException(
                "共同編集を始める前に、メイン画面で .adofai 譜面を開いてください。");
        if (current.SourcePath == "<synthetic>" || !File.Exists(current.SourcePath))
            throw new InvalidOperationException(
                "共同編集には既存の .adofai 譜面が必要です。最初にファイルを開いてください。");
        return AdoFaiLoader.Load(current.SourcePath).Document;
    }

    private void RefreshStatus()
    {
        TogetherWorkspaceDisplay state = _controller.Display;
        _refreshing = true;
        try
        {
            SegmentList.ItemsSource = state.Segments;
            if (state.SelectedSegmentId is string id)
                SegmentList.SelectedItem =
                    state.Segments.FirstOrDefault(s => s.SegmentId == id);
        }
        finally
        {
            _refreshing = false;
        }

        bool loaded = state.WorkspaceDirectory is not null;
        SaveSegmentButton.IsEnabled =
            loaded && state.SelectedSegmentId is not null && !state.HasStagedChanges;
        CommitButton.IsEnabled = loaded && state.HasStagedChanges;
        FetchButton.IsEnabled = loaded;
        PullButton.IsEnabled = loaded && !state.HasStagedChanges;
        PushButton.IsEnabled = loaded && !state.HasStagedChanges;
        ExportButton.IsEnabled = loaded && !state.HasStagedChanges;
        ShareChangesButton.IsEnabled = loaded;
        ReceiveChangesButton.IsEnabled = loaded && !state.HasStagedChanges;
        ReloadViewButton.IsEnabled = loaded;

        if (!loaded)
        {
            TogetherStatusText.Text = _notice ??
                "共同編集はまだ開かれていません。.adofai 譜面を開いてから、作成または参加してください。";
            return;
        }

        string staged = state.HasStagedChanges
            ? "ローカルで保存準備済み（履歴への記録が必要）"
            : "保存準備中の変更なし";
        string conflicts = state.ContentConflictSegmentIds.Count == 0 &&
                           state.TimelineConflicts.Count == 0
            ? string.Empty
            : $" ／ 競合あり: {string.Join("、", state.ContentConflictSegmentIds)}（編集内容は保持）";
        string prefix =
            $"作業フォルダー: {state.WorkspaceDirectory} ／ 履歴: {ShortSha(state.CurrentCommit)} ／ {staged}{conflicts}";
        TogetherStatusText.Text =
            _notice is null ? prefix : $"{_notice}\n{prefix}";
    }

    private void Safely(Action action)
    {
        _notice = null;
        try
        {
            action();
            RefreshStatus();
        }
        catch (OperationCanceledException)
        {
            _notice = "操作をキャンセルしました。編集内容はそのまま残っています。";
            RefreshStatus();
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or ArgumentException or
            InvalidDataException or UnauthorizedAccessException or OverflowException)
        {
            _notice = LocalizeError(ex);
            RefreshStatus();
            TogetherStatusText.ToolTip = ex.ToString();
            MessageBox.Show(this, _notice, "ExtremeEditor 共同編集",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string LocalizeError(Exception error)
    {
        string technical = error.Message;
        if (technical.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) ||
            technical.Contains("non fast forward", StringComparison.OrdinalIgnoreCase) ||
            technical.Contains("fetch first", StringComparison.OrdinalIgnoreCase))
            return "共有先に新しい変更があります。送信を拒否しました。編集内容は保持されています。必要な変更を取り込んでから再度共有してください。";
        if (technical.Contains("remote", StringComparison.OrdinalIgnoreCase) ||
            technical.Contains("origin", StringComparison.OrdinalIgnoreCase) ||
            technical.Contains("bare", StringComparison.OrdinalIgnoreCase))
            return "共有先のGitフォルダーが設定されていない、または利用できません。「詳細設定」でローカル共有フォルダーのパスを確認してください。編集内容は保持されています。";
        if (technical.Contains("uncommitted", StringComparison.OrdinalIgnoreCase) ||
            technical.Contains("dirty", StringComparison.OrdinalIgnoreCase) ||
            technical.Contains("unsaved", StringComparison.OrdinalIgnoreCase))
            return "未保存の編集があるため操作を中止しました。担当区間の変更を保存してから操作してください。";
        if (technical.Contains("Segment", StringComparison.Ordinal) ||
            technical.Contains("ownership", StringComparison.OrdinalIgnoreCase))
            return "担当区間以外の変更、または区間の割り当てを確認できないため、操作を中止しました。編集内容は保持されています。";
        if (technical.Contains("No such", StringComparison.OrdinalIgnoreCase) ||
            technical.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return "指定された作業フォルダーまたは対象が見つかりません。入力内容を確認してください。";
        // Validation from the UI already uses Japanese wording. Keep it.
        if (technical.Any(c => c >= '\u3040' && c <= '\u9fff'))
            return technical;
        return "共同編集の処理を完了できませんでした。編集内容は保持されています。詳細なエラー情報は下部の状態表示にカーソルを合わせると確認できます。";
    }

    private string RequiredOwner()
    {
        string owner = OwnerIdBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("自分の識別名を入力してください。");
        return owner;
    }

    private static string RequiredPath(string? text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException($"{label}のパスを入力してください。");
        return text.Trim();
    }

    private static int[] ParseCuts(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<int>();
        return text.Split(',', StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out int number)
                ? number
                : throw new ArgumentException("分割する床番号は半角数字をカンマ区切りで入力してください。"))
            .ToArray();
    }

    private static string[] ParseOwners(string text, int count, string current)
    {
        if (!string.IsNullOrWhiteSpace(text))
            return text.Split(',', StringSplitOptions.TrimEntries);
        if (count == 1) return [current];

        var owners = new string[count];
        owners[0] = current;
        for (int i = 1; i < count; i++)
            owners[i] = $"編集者{i + 1}";
        return owners;
    }

    private static string ShortSha(string? revision) =>
        string.IsNullOrWhiteSpace(revision) ? "未登録" :
        revision[..Math.Min(12, revision.Length)];

    private static long ToSample(double seconds, int sampleRate) =>
        checked((long)Math.Round(seconds * sampleRate,
            MidpointRounding.AwayFromZero));
}

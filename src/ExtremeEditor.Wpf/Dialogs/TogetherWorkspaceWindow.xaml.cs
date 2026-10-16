using System.IO;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using Microsoft.Win32;

namespace ExtremeEditor.Wpf;

/// <summary>
/// Japanese-first, local-Git Together UI. Synchronizing operations remain
/// explicit. Time-first authored tiles have their own owner-scoped editor;
/// legacy floor-aligned editing remains in MainWindow's EditorSession.
/// This PoC has NO invite URLs, cloud service or realtime remote transport.
/// </summary>
public partial class TogetherWorkspaceWindow : Window
{
    private readonly MainWindow _host;
    private readonly TogetherWorkspaceController _controller = new();
    private TimeFirstSegmentGitWorkspace? _timeWorkspace;
    private TimeFirstSegmentSnapshot? _timeSnapshot;
    private string? _timeOwnerId;
    private string? _timeSelectedSegmentId;
    private TimeFirstSegmentEditorSession? _timeEditor;
    private bool _timeStaged;
    private bool _refreshing;
    private bool _updatingTimelineControls;
    private string? _notice;
    private AudioWaveformData? _timelineWaveform;
    private MusicTimingMap? _musicTimingMap;
    private SegmentTimeTimeline? _plannedTimeline;
    private TogetherSectionPlan? _sectionPlan;
    private string? _selectedPlanSectionId;
    private string? _timelineAudioPath;
    private long? _selectionStartSample;
    private long? _selectionEndSampleExclusive;
    private AudioPlayer? _previewPlayer;
    private int _previewLoadVersion;
    private bool _previewClosed;
    private readonly DispatcherTimer _previewTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(40)
    };

    internal TogetherWorkspaceWindow(MainWindow host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Owner = host;
        InitializeComponent();
        MusicTimeline.ViewportChanged += (_, _) =>
        {
            UpdateTimelineNavigation();
            UpdatePreviewHead();
        };
        _previewTimer.Tick += PreviewTimerTick;
        RefreshStatus();
    }

    private async void TogetherWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_timelineWaveform is not null)
            return;
        await SafelyAsync(async () =>
        {
            string? songPath = _host.TogetherCurrentLevel?.ResolveSongPath();
            if (songPath is null || !File.Exists(songPath))
            {
                MeasuredAudioText.Text =
                    "音源が未選択です。「音源を選択…」から読み込んでください";
                return;
            }
            await LoadMusicTimelineAsync(songPath);
        });
    }

    private async void BrowseTimelineAudioClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "音楽タイムラインに表示する音源を選択",
            Filter = "音声ファイル (*.wav;*.ogg;*.mp3)|*.wav;*.ogg;*.mp3|すべてのファイル (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;
        await SafelyAsync(() => LoadMusicTimelineAsync(dialog.FileName));
    }

    private async Task LoadMusicTimelineAsync(string audioPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        string fullPath = Path.GetFullPath(audioPath);
        int loadVersion = ++_previewLoadVersion;
        _previewTimer.Stop();
        _previewPlayer?.Pause();
        if (_timeWorkspace is null &&
            _sectionPlan is not null &&
            _timelineAudioPath is not null &&
            !string.Equals(_timelineAudioPath, fullPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "作成予定の担当区間があるため、別の音源へ切り替えません。区間を保持したまま新しい共同編集画面を開いてください。");
        }
        MeasuredAudioText.Text = "音源を解析しています…";
        AudioWaveformData waveform = await Task.Run(() =>
            AudioWaveformLoader.Load(fullPath));
        if (_previewClosed || loadVersion != _previewLoadVersion)
            return;
        ValidateLoadedTimeAudio(fullPath, waveform);
        if (_sectionPlan is not null &&
            _sectionPlan.SampleRate != waveform.SampleRate)
        {
            throw new InvalidOperationException(
                "作成予定の担当区間と音源のサンプル周波数が異なるため、区間を破棄せず読み込みを中止しました。");
        }
        // Audio decoding/initialization must not block the WPF dispatcher.
        // Use the existing device-clock AudioPlayer without hit-sound assets.
        PreviewAudioStatusText.Text = "試聴エンジンを準備中…";
        AudioPlayer? preview = null;
        string? previewError = null;
        try
        {
            preview = await Task.Run(() =>
            {
                var player = new AudioPlayer(loadHitSoundAssets: false);
                try
                {
                    player.Load(fullPath);
                    return player;
                }
                catch
                {
                    player.Dispose();
                    throw;
                }
            });
        }
        catch (Exception ex)
        {
            // Devices may be unavailable, but the waveform editor still works.
            previewError = ex.Message;
        }
        if (_previewClosed || loadVersion != _previewLoadVersion)
        {
            preview?.Dispose();
            return;
        }
        _previewPlayer?.Dispose();
        _previewPlayer = preview;
        PreviewAudioStatusText.Text = previewError is null
            ? "クリックでシーク / Spaceで再生・一時停止"
            : $"試聴できません: {previewError}";
        PreviewPlayPauseButton.IsEnabled = _previewPlayer is not null;
        PreviewStopButton.IsEnabled = _previewPlayer is not null;
        PreviewPlayPauseButton.Content = "再生 ▶";
        _timelineWaveform = waveform;
        _timelineAudioPath = fullPath;

        LevelDocument? level = _host.TogetherCurrentLevel;
        if (level is not null && level.InitialBpm > 0 &&
            string.Equals(BpmBox.Text, "120", StringComparison.Ordinal))
            BpmBox.Text = level.InitialBpm.ToString(
                "0.########", CultureInfo.InvariantCulture);

        _musicTimingMap = _timeSnapshot is not null
            ? MusicTimingMap.Build(waveform.SampleRate, _timeSnapshot.TimingPoints)
            : BuildMusicTimingMap(waveform);
        if (_timeWorkspace is null && _sectionPlan is null)
            _sectionPlan = TogetherSectionPlanner.CreateWholeSong(
                waveform.SampleRate, waveform.TotalSamples);
        if (_sectionPlan is not null)
            _plannedTimeline = PlanDisplayTimeline(_sectionPlan);

        SegmentTimeTimeline displayTimeline = _plannedTimeline ??
            new SegmentTimeTimeline(waveform.SampleRate,
                Array.Empty<SegmentTimePartition>());
        MusicTimeline.Load(
            waveform,
            _musicTimingMap,
            displayTimeline.Partitions);
        MusicTimeline.SetSubdivision(SelectedSubdivision());

        SampleRateBox.Text = waveform.SampleRate.ToString(CultureInfo.InvariantCulture);
        ConfirmSampleRateBox.IsChecked = true;
        double seconds = (double)waveform.TotalSamples / waveform.SampleRate;
        MeasuredAudioText.Text =
            $"実測 {waveform.SampleRate:N0} Hz ／ {TimeSpan.FromSeconds(seconds):mm\\:ss\\.fff} ／ {Path.GetFileName(fullPath)}";
        _notice = _sectionPlan is null
            ? "音源を読み込みました。"
            : "曲全体を1つの区間にしました。再生位置を合わせて「現在位置で区切る」を押してください。";
        RefreshParticipantChoices();
        UpdateTimelineNavigation();
        UpdatePreviewHead();
        // Return keyboard focus from setup fields to the playable timeline.
        // Without this, Space can be interpreted as text input after loading.
        MusicTimeline.Focus();
    }

    private void TogetherWindowClosed(object? sender, EventArgs e)
    {
        _previewClosed = true;
        ++_previewLoadVersion;
        _previewTimer.Stop();
        _previewPlayer?.Dispose();
        _previewPlayer = null;
    }

    private void TogetherPreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key != Key.Space)
            return;
        // Space is a global preview shortcut, except while editing text.
        // A ComboBox or button may keep focus after being clicked; neither
        // should prevent playback from toggling.
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox)
            return;
        e.Handled = true;
        Safely(PreviewTogglePlayback);
    }

    private void PreviewPlayPauseClick(object sender, RoutedEventArgs e) =>
        Safely(PreviewTogglePlayback);

    private void PreviewStopClick(object sender, RoutedEventArgs e) => Safely(() =>
    {
        _previewTimer.Stop();
        _previewPlayer?.Stop();
        UpdatePreviewHead();
    });

    private void PreviewTogglePlayback()
    {
        if (_previewPlayer is null || _timelineWaveform is null)
            return;
        if (_previewPlayer.IsPlaying)
        {
            _previewPlayer.Pause();
            _previewTimer.Stop();
        }
        else
        {
            if (PreviewLoopCheckBox.IsChecked == true &&
                TryGetPreviewLoop(out long begin, out long end))
            {
                long position = PreviewSamplePosition();
                if (position < begin || position >= end)
                    SeekPreview(begin);
            }
            _previewPlayer.Play();
            _previewTimer.Start();
        }
        UpdatePreviewHead();
    }

    private void MusicTimelineSeekRequested(
        object? sender, TogetherTimelineSeekRequestedEventArgs e) =>
        Safely(() =>
        {
            SeekPreview(e.AudioSample);
            UpdatePreviewHead();
        });

    private void SeekPreview(long sample)
    {
        if (_previewPlayer is null || _timelineWaveform is null)
            return;
        long clamped = Math.Clamp(sample, 0, _timelineWaveform.TotalSamples);
        _previewPlayer.Seek(TimeSpan.FromSeconds(
            (double)clamped / _timelineWaveform.SampleRate));
    }

    private long PreviewSamplePosition()
    {
        if (_previewPlayer is null || _timelineWaveform is null)
            return 0;
        return Math.Clamp(checked((long)Math.Round(
                _previewPlayer.Position.TotalSeconds * _timelineWaveform.SampleRate,
                MidpointRounding.AwayFromZero)),
            0, _timelineWaveform.TotalSamples);
    }

    private bool TryGetPreviewLoop(out long start, out long end)
    {
        start = end = 0;
        if (_selectionStartSample is long selectedStart &&
            _selectionEndSampleExclusive is long selectedEnd &&
            selectedEnd > selectedStart)
        {
            start = selectedStart;
            end = selectedEnd;
            return true;
        }
        SegmentTimePartition? segment = _plannedTimeline?.Partitions.FirstOrDefault(
            p => string.Equals(p.SegmentId, _timeSelectedSegmentId,
                StringComparison.Ordinal));
        if (segment is null)
            return false;
        start = segment.StartSample;
        end = segment.EndSampleExclusive;
        return true;
    }

    private void PreviewTimerTick(object? sender, EventArgs e)
    {
        if (_previewPlayer is null || _timelineWaveform is null)
        {
            _previewTimer.Stop();
            return;
        }
        // Check the loop before EOF handling: a device may already report
        // Stopped when the selected range ends at the final audio sample.
        if (PreviewLoopCheckBox.IsChecked == true &&
            TryGetPreviewLoop(out long begin, out long end) &&
            PreviewSamplePosition() >= end)
        {
            SeekPreview(begin);
            if (!_previewPlayer.IsPlaying)
                _previewPlayer.Play();
        }
        if (!_previewPlayer.IsPlaying)
            _previewTimer.Stop();
        UpdatePreviewHead();
    }

    private void UpdatePreviewHead()
    {
        long position = PreviewSamplePosition();
        if (_previewPlayer is null || _timelineWaveform is null)
        {
            PreviewPlayhead.Visibility = Visibility.Collapsed;
            PreviewPositionText.Text = "00:00.000 / 00:00.000";
            PreviewPlayPauseButton.Content = "再生 ▶";
            return;
        }
        PreviewPlayPauseButton.Content = _previewPlayer.IsPlaying
            ? "一時停止 ❚❚" : "再生 ▶";
        PreviewPositionText.Text =
            $"{FormatPreviewTime(position)} / {FormatPreviewTime(_timelineWaveform.TotalSamples)}";
        double x = (position - MusicTimeline.VisibleStartSample) /
            MusicTimeline.SamplesPerPixel;
        bool visible = x >= 0 && x <= MusicTimeline.ActualWidth;
        PreviewPlayhead.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
            Canvas.SetLeft(PreviewPlayhead, x);
    }

    private string FormatPreviewTime(long sample) =>
        _timelineWaveform is null ? "00:00.000" :
        TimeSpan.FromSeconds((double)sample / _timelineWaveform.SampleRate)
            .ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);

    private void ValidateLoadedTimeAudio(string path, AudioWaveformData waveform)
    {
        if (_timeSnapshot is not TimeFirstSegmentSnapshot committed) return;
        if (waveform.SampleRate != committed.Audio.SampleRate ||
            waveform.TotalSamples != committed.Audio.TotalSamples ||
            !string.Equals(Path.GetFileName(path), committed.Audio.FileName,
                StringComparison.Ordinal) ||
            !string.Equals(ComputeSha256(path), committed.Audio.Sha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "この音源は共同編集に保存された音源と一致しません（名前・サンプル周波数・長さ・SHA-256）。既存の担当区間を保持しています。");
    }

    private MusicTimingMap BuildMusicTimingMap(AudioWaveformData waveform)
    {
        if (!decimal.TryParse(BpmBox.Text.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out decimal bpm) || bpm <= 0)
            throw new ArgumentException("BPMには0より大きい数値を入力してください。");
        if (!decimal.TryParse(BeatOffsetBox.Text.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out decimal offsetMilliseconds))
            throw new ArgumentException("拍オフセットにはミリ秒を数値で入力してください。");
        decimal sample = decimal.Round(
            offsetMilliseconds * waveform.SampleRate / 1000m,
            0,
            MidpointRounding.AwayFromZero);
        if (sample < long.MinValue || sample > long.MaxValue)
            throw new OverflowException("拍オフセットが音声サンプル範囲を超えています。");
        return MusicTimingMap.Build(waveform.SampleRate,
            [new MusicTimingPoint((long)sample, bpm)]);
    }

    private void MusicGridSettingsChanged(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            if (_timelineWaveform is null)
                return;
            if (_timeWorkspace is not null)
                throw new InvalidOperationException(
                    "作成済みWorkspaceの音楽グリッドは履歴から復元されます。ここでの変更はできません。");
            _musicTimingMap = BuildMusicTimingMap(_timelineWaveform);
            MusicTimeline.SetTimingMap(_musicTimingMap);
            _notice = "BPMと拍オフセットを音楽グリッドに反映しました。";
        });

    private void SnapSubdivisionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
            return;
        MusicTimeline.SetSubdivision(SelectedSubdivision());
    }

    private MusicBeatSubdivision SelectedSubdivision()
    {
        string? tag = (SnapSubdivisionBox.SelectedItem as ComboBoxItem)?.Tag as string;
        return tag switch
        {
            "1" => MusicBeatSubdivision.One,
            "2" => MusicBeatSubdivision.Half,
            "3" => MusicBeatSubdivision.Third,
            "4" => MusicBeatSubdivision.Quarter,
            "8" => MusicBeatSubdivision.Eighth,
            "16" => MusicBeatSubdivision.Sixteenth,
            _ => MusicBeatSubdivision.Quarter
        };
    }

    private void MusicTimelineSelectionChanged(
        object? sender,
        TogetherTimelineSelectionChangedEventArgs e)
    {
        _selectionStartSample = e.StartSample;
        _selectionEndSampleExclusive = e.EndSampleExclusive;
        RefreshSelectedInterval();
    }

    private void MusicTimelineEndpointRequested(
        object? sender, TogetherTimelineEndpointRequestedEventArgs e) =>
        SetSelectionEndpoint(e.Endpoint, e.AudioSample);

    private void PreviewSetStartClick(object sender, RoutedEventArgs e) =>
        Safely(() => SetSelectionEndpointAtPreview(TogetherTimelineEndpoint.Start));

    private void PreviewSetEndClick(object sender, RoutedEventArgs e) =>
        Safely(() => SetSelectionEndpointAtPreview(TogetherTimelineEndpoint.End));

    private void SetSelectionEndpointAtPreview(TogetherTimelineEndpoint endpoint)
    {
        if (_musicTimingMap is null || _timelineWaveform is null)
            throw new InvalidOperationException("先に音源を読み込んでください。");
        long snap = _musicTimingMap.Snap(
            PreviewSamplePosition(), SelectedSubdivision()).AudioSample;
        SetSelectionEndpoint(endpoint,
            Math.Clamp(snap, 0, _timelineWaveform.TotalSamples));
    }

    private void SetSelectionEndpoint(TogetherTimelineEndpoint endpoint, long sample)
    {
        if (endpoint == TogetherTimelineEndpoint.Start)
            _selectionStartSample = sample;
        else
            _selectionEndSampleExclusive = sample;
        RefreshSelectedInterval();
    }

    private void RefreshSelectedInterval()
    {
        SelectionStartSampleText.Text = _selectionStartSample?.ToString("N0") ?? "―";
        SelectionEndSampleText.Text = _selectionEndSampleExclusive?.ToString("N0") ?? "―";
        MusicTimeline.SetSelectionEndpoints(
            _selectionStartSample, _selectionEndSampleExclusive);
        _notice = _selectionStartSample is long start &&
                  _selectionEndSampleExclusive is long end
            ? end > start
                ? $"音楽上の範囲 [{start:N0}, {end:N0}) samples を選択しました。"
                : "終点は始点より後に指定してください。"
            : "始点・終点を個別に指定できます（Shift+クリック／Alt+クリック）。";
        RefreshStatus();
    }

    private void AddTimeSegmentClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            if (_sectionPlan is null || _timelineWaveform is null)
                throw new InvalidOperationException(
                    "先に音源を読み込んで時間範囲を選択してください。");
            if (_selectionStartSample is not long start ||
                _selectionEndSampleExclusive is not long end || end <= start)
                throw new InvalidOperationException(
                    "始点と終点を指定し、終点を始点より後にしてください。");
            string owner = SegmentOwnerBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(owner))
                throw new ArgumentException("担当者名を入力してください。");

            SplitPlanAt(start);
            SplitPlanAt(end);
            TogetherSection section = _sectionPlan.Sections.Single(item =>
                item.StartSample == start && item.EndSampleExclusive == end);
            _sectionPlan = TogetherSectionPlanner.AssignOwner(
                _sectionPlan, section.InternalId, owner);
            _selectedPlanSectionId = section.InternalId;
            ApplySectionPlan($"選択範囲を「{owner}」の担当区間に設定しました。");
        });

    private void SplitSectionClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            TogetherSectionPlan plan = RequiredSectionPlan();
            long sample = PlanningSample();
            TogetherSection section = plan.Sections.SingleOrDefault(item =>
                sample > item.StartSample && sample < item.EndSampleExclusive)
                ?? throw new InvalidOperationException(
                    "区切り位置を区間の途中に合わせてください。曲の先頭・末尾では分割できません。");
            _sectionPlan = TogetherSectionPlanner.Split(
                plan, section.InternalId, sample);
            _selectedPlanSectionId = section.InternalId;
            ApplySectionPlan("現在位置で曲を区切りました。続けて各区間の担当者を設定してください。");
        });

    private void JoinSectionClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            TogetherSectionPlan plan = RequiredSectionPlan();
            string id = RequiredPlannedSelection();
            _sectionPlan = TogetherSectionPlanner.JoinWithNext(plan, id);
            _selectedPlanSectionId = id;
            ApplySectionPlan("選択した区間を次の区間と結合しました。担当者が異なっていた場合は再設定してください。");
        });

    private void MoveBoundaryClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            TogetherSectionPlan plan = RequiredSectionPlan();
            string id = RequiredPlannedSelection();
            _sectionPlan = TogetherSectionPlanner.MoveBoundary(
                plan, id, PlanningSample());
            ApplySectionPlan("選択した区間の右境界を移動しました。既存の床や編集内容は変更していません。");
        });

    private void AssignOwnerClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            TogetherSectionPlan plan = RequiredSectionPlan();
            string owner = SegmentOwnerBox.Text.Trim();
            _sectionPlan = TogetherSectionPlanner.AssignOwner(
                plan, RequiredPlannedSelection(), owner);
            ApplySectionPlan($"選択した区間の担当者を「{owner}」に設定しました。");
            RefreshParticipantChoices();
        });

    private void TimelineZoomChanged(
        object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingTimelineControls || !IsInitialized)
            return;
        MusicTimeline.SetZoomLevel(e.NewValue);
    }

    private void TimelineScrollChanged(
        object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingTimelineControls || !IsInitialized)
            return;
        MusicTimeline.SetScrollSample(
            checked((long)Math.Round(e.NewValue,
                MidpointRounding.AwayFromZero)));
    }

    private void UpdateTimelineNavigation()
    {
        if (MusicTimeline.TotalSamples <= 0)
            return;
        _updatingTimelineControls = true;
        try
        {
            long visible = MusicTimeline.VisibleEndSampleExclusive -
                MusicTimeline.VisibleStartSample;
            long maximum = Math.Max(0, MusicTimeline.TotalSamples - visible);
            TimelineScrollBar.Maximum = maximum;
            TimelineScrollBar.ViewportSize = visible;
            TimelineScrollBar.SmallChange = Math.Max(
                1, MusicTimeline.SamplesPerPixel * 40);
            TimelineScrollBar.LargeChange = Math.Max(1, visible * 0.8);
            TimelineScrollBar.Value = Math.Clamp(
                MusicTimeline.VisibleStartSample, 0, maximum);
            // Ctrl+wheel also adjusts the slider without re-zooming around
            // the slider's center point.
            TimelineZoomSlider.Value = MusicTimeline.ZoomLevel;
        }
        finally
        {
            _updatingTimelineControls = false;
        }
    }

    private static string NextPlannedSegmentId(SegmentTimeTimeline timeline)
    {
        int number = timeline.Partitions.Count + 1;
        while (timeline.Partitions.Any(partition => string.Equals(
                   partition.SegmentId, $"segment-{number}",
                   StringComparison.Ordinal)))
            number++;
        return $"segment-{number}";
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
        CloneWorkspaceButton.Visibility =
            forCreation ? Visibility.Collapsed : Visibility.Visible;
        _notice = forCreation
            ? "音源を選び、区間ごとの担当者を設定してから保存先を指定してください。"
            : "受け取った共同編集フォルダーを指定して開いてください。";
        RefreshStatus();
        WorkspacePathBox.Focus();
    }

    private void CreateWorkspaceClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            RefuseWorkspaceReplacement();
            string workspacePath = RequiredPath(
                WorkspacePathBox.Text, "保存先フォルダー");
            TogetherSectionPlan plan = RequiredSectionPlan();
            TogetherSection? missing = plan.Sections.FirstOrDefault(
                section => string.IsNullOrWhiteSpace(section.OwnerName));
            if (missing is not null)
            {
                int number = plan.Sections.ToList().IndexOf(missing) + 1;
                throw new InvalidOperationException(
                    $"区間 {number} の担当者が未設定です。区間を選び、担当者名を設定してください。");
            }

            _plannedTimeline = TogetherSectionPlanner.FinalizeTimeline(plan);
            string owner = plan.Sections[0].OwnerName!;
            CreateTimeFirstWorkspace(workspacePath, plan.SampleRate, owner);
            SetupPanel.Visibility = Visibility.Collapsed;
            ParticipantNameBox.Text = owner;
            _notice =
                "共同編集を作成しました。空の区間もそのまま保存されています。共有先の設定は「詳細設定」から行えます。";
        });

    private void OpenWorkspaceClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            RefuseWorkspaceReplacement();
            string path = RequiredPath(WorkspacePathBox.Text, "作業フォルダー");
            bool timeFirst = File.Exists(Path.Combine(path, "time-first-workspace.json"));
            if (timeFirst)
            {
                OpenTimeFirstWorkspace(path);
                _notice =
                    "共同編集を開きました。参加者名を選んで「この名前で編集」を押してください。";
            }
            else
            {
                string owner = RequiredOwner();
                _controller.Open(path, ReadSourceTemplate(), owner);
                LoadIntoEditor();
                _notice = "共同編集に参加しました。自分の担当区間を選んで編集してください。";
                SetupPanel.Visibility = Visibility.Collapsed;
            }
        });

    private void CloneWorkspaceClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            RefuseWorkspaceReplacement();
            string remote = RequiredPath(BareRemoteBox.Text, "Git共有フォルダー");
            string destination = RequiredPath(WorkspacePathBox.Text, "複製先の作業フォルダー");
            TimeFirstSegmentGitWorkspace cloned =
                TimeFirstSegmentGitRemoteSync.CloneLocalBare(remote, destination);
            OpenTimeFirstWorkspace(cloned.Directory);
            _notice = "共同編集を複製しました。参加者名を選んでください。";
        });

    private void ChooseParticipantClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            string owner = RequiredOwner();
            if (_timeSnapshot is null)
                throw new InvalidOperationException("先に既存の共同編集を開いてください。");
            if (!_timeSnapshot.Timeline.Partitions.Any(partition =>
                    partition.Status == SegmentPartitionStatus.Active &&
                    string.Equals(partition.OwnerId, owner, StringComparison.Ordinal)))
                throw new ArgumentException(
                    "その名前の担当区間はありません。担当者一覧から自分の名前を選んでください。");
            RefuseUnsafeTimeEditorSwitch();
            _timeOwnerId = owner;
            _timeSelectedSegmentId = null;
            _timeEditor = null;
            SetupPanel.Visibility = Visibility.Collapsed;
            _notice = $"「{owner}」として参加しました。自分の担当区間を選ぶと編集できます。";
        });

    /// <summary>
    /// Do not replace an editor session while it owns unsaved authored tiles,
    /// or replace a staged snapshot before it is committed. Neither case may
    /// silently discard work during Join/Clone/Pull/segment selection.
    /// </summary>
    private void RefuseWorkspaceReplacement()
    {
        RefuseUnsafeTimeEditorSwitch();
        if (_timeWorkspace is not null ||
            _controller.Display.WorkspaceDirectory is not null)
            throw new InvalidOperationException(
                "別のWorkspaceに切り替えるには共同編集ウィンドウを開き直してください。現在の作業内容は保持しています。");
    }

    private void RefuseUnsafeTimeEditorSwitch()
    {
        if (_timeEditor?.IsDirty == true)
            throw new InvalidOperationException(
                "担当区間に未保存の編集があります。保存準備と履歴記録を済ませてから切り替えてください。");
        if (_timeStaged)
            throw new InvalidOperationException(
                "保存準備済みの時間区間があるため切り替えません。先に履歴へ記録してください。");
    }

    private void ReloadViewClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            if (_timeWorkspace is not null)
            {
                RefuseUnsafeTimeEditorSwitch();
                ApplyTimeFirstSnapshot(_timeWorkspace.Load());
                _notice = "Gitの記録内容から時間区間と担当を再読込しました。";
            }
            else
            {
                LoadIntoEditor();
                _notice = "Gitの記録内容から譜面を再表示しました。";
            }
        });

    private void SegmentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || SegmentList.SelectedItem is not TogetherSegmentDisplay segment)
            return;

        if (_timeWorkspace is not null)
        {
            if (string.Equals(_timeSelectedSegmentId, segment.SegmentId,
                StringComparison.Ordinal))
                return;
            if (_timeEditor?.IsDirty == true || _timeStaged)
            {
                // Restore the previous row rather than losing the undo stack.
                _refreshing = true;
                try
                {
                    SegmentList.SelectedItem = SegmentList.Items.Cast<TogetherSegmentDisplay>()
                        .FirstOrDefault(row => row.SegmentId == _timeSelectedSegmentId);
                }
                finally { _refreshing = false; }
                _notice = "保存準備・履歴記録が済むまでは担当区間を切り替えられません。";
                RefreshStatus();
                return;
            }
            _timeSelectedSegmentId = segment.SegmentId;
            if (segment.CanEdit)
            {
                _timeEditor = TimeFirstSegmentEditorSession.Open(
                    _timeSnapshot!, segment.SegmentId, _timeOwnerId!);
                AuthoredTileList.ItemsSource = _timeEditor.Current.Tiles;
                EntryAngleBox.Text = _timeEditor.Current.EntryAngle.ToString(
                    "0.########", CultureInfo.InvariantCulture);
            }
            else
            {
                _timeEditor = null;
                AuthoredTileList.ItemsSource =
                    _timeSnapshot?.Authored is not null &&
                    _timeSnapshot.Authored.TryGetValue(
                        segment.SegmentId, out TimeFirstAuthoredSegment? authored)
                        ? authored.Tiles : Array.Empty<TimeFirstAuthoredTile>();
            }
            _notice = segment.CanEdit
                ? $"担当区間「{segment.SegmentId}」を開きました。タイムラインで位置を選び、角度を指定して実タイルを配置できます。"
                : $"「{segment.SegmentId}」は他の担当者の区間です。閲覧のみ可能です。";
            RefreshStatus();
            return;
        }

        if (_controller.Display.WorkspaceDirectory is null &&
            _sectionPlan?.Sections.Any(section => string.Equals(
                section.InternalId, segment.SegmentId,
                StringComparison.Ordinal)) == true)
        {
            _selectedPlanSectionId = segment.SegmentId;
            SegmentOwnerBox.Text = segment.OwnerId ?? string.Empty;
            _notice = $"{segment.DisplayName}を選択しました。担当者の設定、結合、境界移動ができます。";
            RefreshStatus();
            return;
        }

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
            if (_timeWorkspace is not null)
            {
                StageTimeEditor();
                _notice =
                    "角度と時刻アンカーを含む担当区間を保存準備しました。履歴への記録後に共有できます。";
                return;
            }
            EditorSession editor = RequiredEditor();
            _controller.StageSelected(editor);
            _notice = "選択中の担当区間を保存準備しました。共有するには履歴に記録して送信してください。";
        });

    private void CommitClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            if (_timeWorkspace is not null)
            {
                if (!_timeStaged)
                    throw new InvalidOperationException(
                        "履歴へ記録する時間区間編集がありません。");
                string timeRevision = CommitTimeEditor();
                _notice =
                    $"時間区間の変更をローカル履歴に記録しました（{ShortSha(timeRevision)}）。";
                return;
            }
            string revision = _controller.Commit(CommitMessageBox.Text.Trim());
            _notice = $"変更をローカル履歴に記録しました（{ShortSha(revision)}）。共有先への送信はまだです。";
        });

    private void SetEntryAngleClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            if (_timeStaged)
                throw new InvalidOperationException("先に保存準備済みの編集を履歴へ記録してください。");
            TimeFirstSegmentEditorSession editor = _timeEditor ??
                throw new InvalidOperationException("自分の担当時間区間を選択してください。");
            double entry = ParseFiniteDouble(EntryAngleBox.Text, "入口角度");
            editor.SetEntryAngle(entry);
            EntryAngleBox.Text = editor.Current.EntryAngle.ToString(
                "0.########", CultureInfo.InvariantCulture);
            _notice = $"区間の入口角度を {editor.Current.EntryAngle:0.###}° に設定しました。";
        });

    private void PlaceTimeTileClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            if (_timeStaged)
                throw new InvalidOperationException("先に保存準備済みの編集を履歴へ記録してください。");
            TimeFirstSegmentEditorSession editor = _timeEditor ??
                throw new InvalidOperationException(
                    "先に自分の担当時間区間を選択してください。");
            if (_selectionStartSample is not long sample)
                throw new InvalidOperationException(
                    "タイムライン上で配置時刻を選択してください。");
            if (!double.TryParse(TimeTileAngleBox.Text.Trim(),
                    NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double angle) || !double.IsFinite(angle))
                throw new ArgumentException("角度を有限の数値で入力してください。");
            string tileId = TimeTileIdBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(tileId))
            {
                int number = editor.Current.Tiles.Count + 1;
                do { tileId = $"tile-{number++}"; }
                while (editor.Current.Tiles.Any(tile => tile.TileId == tileId));
            }
            editor.PlaceTile(tileId, sample, angle);
            TimeTileIdBox.Clear();
            RefreshTimeTileEditor();
            _notice = $"実タイル「{tileId}」を {sample:N0} sample、角度 {angle:0.###}° で配置しました。";
        });

    private void RemoveTimeTileClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            if (_timeStaged)
                throw new InvalidOperationException("先に保存準備済みの編集を履歴へ記録してください。");
            TimeFirstSegmentEditorSession editor = _timeEditor ??
                throw new InvalidOperationException(
                    "先に自分の担当時間区間を選択してください。");
            if (AuthoredTileList.SelectedItem is not TimeFirstAuthoredTile tile)
                throw new InvalidOperationException("削除する実タイルを一覧から選択してください。");
            editor.RemoveTile(tile.TileId);
            RefreshTimeTileEditor();
            _notice = $"実タイル「{tile.TileId}」を削除しました。";
        });

    private void UndoTimeTileClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            if (_timeStaged)
                throw new InvalidOperationException("先に保存準備済みの編集を履歴へ記録してください。");
            (_timeEditor ?? throw new InvalidOperationException(
                "編集中の担当時間区間がありません。")).Undo();
            RefreshTimeTileEditor();
            _notice = "時間区間のタイル編集を元に戻しました。";
        });

    private void RedoTimeTileClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            if (_timeStaged)
                throw new InvalidOperationException("先に保存準備済みの編集を履歴へ記録してください。");
            (_timeEditor ?? throw new InvalidOperationException(
                "編集中の担当時間区間がありません。")).Redo();
            RefreshTimeTileEditor();
            _notice = "時間区間のタイル編集をやり直しました。";
        });

    private void RefreshTimeTileEditor()
    {
        AuthoredTileList.ItemsSource = null;
        AuthoredTileList.ItemsSource = _timeEditor?.Current.Tiles ??
            Array.Empty<TimeFirstAuthoredTile>();
    }

    private void ConnectClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            string remote = RequiredPath(BareRemoteBox.Text, "Git共有フォルダー");
            if (_timeWorkspace is not null)
                TimeFirstSegmentGitRemoteSync.ConnectLocalBare(_timeWorkspace, remote);
            else
                _controller.ConnectLocalBare(remote);
            _notice = "ローカルGit共有フォルダーに接続しました。変更はまだ送信していません。";
        });

    private void FetchClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            string remote = _timeWorkspace is not null
                ? TimeFirstSegmentGitRemoteSync.Fetch(_timeWorkspace)
                : _controller.Fetch();
            _notice = $"共有先の履歴を取得しました（{ShortSha(remote)}）。作業内容は変更していません。";
        });

    private void PullClick(object sender, RoutedEventArgs e) =>
        Safely(ReceiveChanges);

    private void ReceiveChangesClick(object sender, RoutedEventArgs e) =>
        Safely(ReceiveChanges);

    private void ReceiveChanges()
    {
        if (_timeWorkspace is not null)
        {
            RefuseUnsafeTimeEditorSwitch();
            TimeFirstSegmentPullResult timeResult =
                TimeFirstSegmentGitRemoteSync.Pull(_timeWorkspace);
            if (timeResult.Success)
            {
                ApplyTimeFirstSnapshot(_timeWorkspace.Load());
                _notice = $"時間区間のGit変更を取り込みました（{ShortSha(timeResult.LocalCommit)}）。";
            }
            else
            {
                // Semantic conflicts never overwrite the local HEAD or authored files.
                string ids = string.Join("、", timeResult.ContentConflictSegmentIds);
                string timeline = string.Join("、", timeResult.TimelineConflicts
                    .Select(conflict => conflict.SegmentId));
                _notice = $"時間区間の競合を検出しました。ローカルの編集内容と履歴は保持しています。内容競合: {ids} ／ 割当競合: {timeline}。";
            }
            return;
        }
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
            if (_timeWorkspace is not null)
                RefuseUnsafeTimeEditorSwitch();
            string published = _timeWorkspace is not null
                ? TimeFirstSegmentGitRemoteSync.Push(_timeWorkspace)
                : _controller.Push();
            _notice = $"ローカルGit共有フォルダーへ送信しました（{ShortSha(published)}）。";
        });

    /// <summary>
    /// Explicit user-initiated Stage -> Commit -> Push, never automatic Pull,
    /// force push or rollback. A failed Push leaves the local commit intact.
    /// </summary>
    private void ShareChangesClick(object sender, RoutedEventArgs e) =>
        Safely(() =>
        {
            if (_timeWorkspace is not null)
            {
                if (_timeEditor?.IsDirty == true)
                {
                    if (_timeStaged)
                        throw new InvalidOperationException(
                            "保存準備済みの区間があるため新しい編集を共有できません。");
                    StageTimeEditor();
                }
                if (_timeStaged)
                {
                    CommitTimeEditor();
                }
                // No automatic Pull/rebase. Non-FF Push leaves committed work intact.
                string timePublished = TimeFirstSegmentGitRemoteSync.Push(_timeWorkspace);
                _notice = $"時間区間の編集を共有しました（{ShortSha(timePublished)}）。";
                return;
            }
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
            if (_timeWorkspace is not null)
            {
                RefuseUnsafeTimeEditorSwitch();
                _timeWorkspace.AssertCleanOwnedWorktree();
                TimeFirstAdoFaiSettings settings = ExplicitExportSettings();
                TimeFirstSegmentSnapshot snapshot =
                    _timeWorkspace.ReadAt(_timeWorkspace.HeadCommit);
                TimeFirstAdoFaiCompositionResult preview =
                    TimeFirstSegmentAdoFaiExporter.ComposeAndValidate(snapshot, settings);
                if (!preview.Accepted)
                    throw new InvalidOperationException(
                        "完成譜面の検証に失敗しました。変更は保持されています: " +
                        string.Join(" / ", preview.Issues));

                var timeDialog = new SaveFileDialog
                {
                    Title = "時間区間から完成ADOFAI譜面を書き出す",
                    Filter = "ADOFAI 譜面 (*.adofai)|*.adofai",
                    DefaultExt = ".adofai", AddExtension = true,
                    OverwritePrompt = true,
                    FileName = "Together-完成譜面.adofai"
                };
                if (timeDialog.ShowDialog(this) != true) return;
                TimeFirstAdoFaiExportResult completed =
                    TimeFirstSegmentAdoFaiExporter.ExportValidated(
                        _timeWorkspace, settings, timeDialog.FileName,
                        overwriteExisting: File.Exists(timeDialog.FileName));
                _notice = $"完成譜面を書き出しました（{completed.FloorCount}床、{completed.ActionCount}イベント、履歴 {ShortSha(completed.SourceCommit)}）。保存先: {completed.DestinationPath}";
                return;
            }
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

    private static double ParseFiniteDouble(string value, string label)
    {
        if (!double.TryParse(value.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double number) ||
            !double.IsFinite(number))
            throw new ArgumentException($"{label}には有限の数値を入力してください。");
        return number;
    }

    private TimeFirstAdoFaiSettings ExplicitExportSettings()
    {
        double bpm = ParseFiniteDouble(ExportBpmBox.Text, "ADOFAI BPM");
        double offset = ParseFiniteDouble(ExportOffsetBox.Text, "ADOFAI Offset");
        double pitch = ParseFiniteDouble(ExportPitchBox.Text, "Pitch");
        if (!int.TryParse(ExportCountdownBox.Text.Trim(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int ticks))
            throw new ArgumentException("Countdownには整数を入力してください。");
        return new TimeFirstAdoFaiSettings(bpm, offset, pitch, ticks,
            ExportSeparateCountdownBox.IsChecked == true);
    }

    private string CommitMessage() => string.IsNullOrWhiteSpace(CommitMessageBox.Text)
        ? "担当区間を編集" : CommitMessageBox.Text.Trim();

    // The old session still contains its pre-commit Undo history after Save.
    // Reopen the committed revision so future edits, selection and Pull treat it
    // as clean; never discard this session if the Git commit itself fails.
    private string CommitTimeEditor()
    {
        if (!_timeStaged || _timeWorkspace is null || _timeSnapshot is null ||
            _timeEditor is null || _timeOwnerId is null)
            throw new InvalidOperationException(
                "保存準備済みの担当区間編集がありません。");
        string segmentId = _timeEditor.Current.SegmentId;
        string sha = _timeWorkspace.Commit(CommitMessage());
        _timeEditor = TimeFirstSegmentEditorSession.Open(
            _timeSnapshot, segmentId, _timeOwnerId);
        _timeStaged = false;
        RefreshTimeTileEditor();
        return sha;
    }

    private void StageTimeEditor()
    {
        if (_timeStaged)
            throw new InvalidOperationException("先に保存準備済みの編集を履歴へ記録してください。");
        TimeFirstSegmentEditorSession editor = _timeEditor ??
            throw new InvalidOperationException("自分の担当時間区間を選択してください。");
        if (!editor.IsDirty)
            throw new InvalidOperationException("保存準備する変更がありません。");
        _timeWorkspace!.AssertCleanOwnedWorktree();
        string revision = editor.Current.SegmentId + "-" +
            Guid.NewGuid().ToString("N");
        TimeFirstSegmentSnapshot updated = editor.CreateRevision(_timeSnapshot!, revision);
        _timeWorkspace.Save(updated);
        _timeSnapshot = updated;
        _timeStaged = true;
    }

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
        bool timeLoaded = _timeWorkspace is not null && _timeSnapshot is not null;
        bool loaded = timeLoaded || state.WorkspaceDirectory is not null;
        IReadOnlyList<TogetherSegmentDisplay> rows = state.Segments;
        if ((timeLoaded || !loaded) && _plannedTimeline is not null)
        {
            rows = _plannedTimeline.Partitions
                .OrderBy(partition => partition.StartSample)
                .Select((partition, index) => new TogetherSegmentDisplay(
                    partition.SegmentId,
                    -1,
                    -1,
                    partition.StartSample,
                    partition.EndSampleExclusive,
                    partition.OwnerId,
                    partition.Status,
                    partition.ContentRevisionId,
                    timeLoaded && string.Equals(partition.SegmentId,
                        _timeSelectedSegmentId, StringComparison.Ordinal),
                    timeLoaded
                        ? string.Equals(partition.OwnerId, _timeOwnerId,
                            StringComparison.Ordinal)
                        : true,
                    index + 1,
                    _plannedTimeline.SampleRate))
                .ToArray();
        }
        _refreshing = true;
        try
        {
            SegmentList.ItemsSource = rows;
            string? activeId = timeLoaded
                ? _timeSelectedSegmentId
                : _selectedPlanSectionId ?? state.SelectedSegmentId;
            if (activeId is not null)
                SegmentList.SelectedItem =
                    rows.FirstOrDefault(s => s.SegmentId == activeId);
        }
        finally
        {
            _refreshing = false;
        }

        EntryAngleBox.IsEnabled = timeLoaded && _timeEditor is not null && !_timeStaged;
        SetEntryAngleButton.IsEnabled = EntryAngleBox.IsEnabled;
        BpmBox.IsEnabled = !timeLoaded;
        BeatOffsetBox.IsEnabled = !timeLoaded;
        AddTimeSegmentButton.IsEnabled = !timeLoaded;
        SplitSectionButton.IsEnabled = !loaded && _sectionPlan is not null;
        JoinSectionButton.IsEnabled = !loaded && _selectedPlanSectionId is not null;
        MoveBoundaryButton.IsEnabled = JoinSectionButton.IsEnabled;
        AssignOwnerButton.IsEnabled = JoinSectionButton.IsEnabled;
        ChooseParticipantButton.IsEnabled = timeLoaded;
        CloneWorkspaceButton.IsEnabled = !loaded;
        SaveSegmentButton.IsEnabled = timeLoaded
            ? _timeEditor?.IsDirty == true && !_timeStaged
            : loaded && state.SelectedSegmentId is not null && !state.HasStagedChanges;
        CommitButton.IsEnabled = timeLoaded
            ? _timeStaged
            : loaded && state.HasStagedChanges;
        FetchButton.IsEnabled = loaded;
        PullButton.IsEnabled = loaded && (timeLoaded
            ? !_timeStaged && _timeEditor?.IsDirty != true
            : !state.HasStagedChanges);
        PushButton.IsEnabled = PullButton.IsEnabled;
        ExportButton.IsEnabled = PullButton.IsEnabled;
        ShareChangesButton.IsEnabled = loaded;
        ReceiveChangesButton.IsEnabled = PullButton.IsEnabled;
        ReloadViewButton.IsEnabled = loaded;
        PlaceTimeTileButton.IsEnabled = timeLoaded && _timeEditor is not null && !_timeStaged;
        RemoveTimeTileButton.IsEnabled = timeLoaded && _timeEditor is not null && !_timeStaged;
        UndoTimeTileButton.IsEnabled = timeLoaded && _timeEditor?.CanUndo == true && !_timeStaged;
        RedoTimeTileButton.IsEnabled = timeLoaded && _timeEditor?.CanRedo == true && !_timeStaged;

        if (!loaded)
        {
            string planned = _sectionPlan is { Sections.Count: > 0 }
                ? $"作成予定の区間: {_sectionPlan.Sections.Count}件"
                : "共同編集はまだ開かれていません";
            TogetherStatusText.Text = _notice ??
                $"{planned}。音源を読み込み、曲を区切って担当者を設定してください。";
            return;
        }

        string staged = timeLoaded
            ? (_timeStaged ? "時間区間の編集を保存準備済み（Commitが必要）"
                : _timeEditor?.IsDirty == true ? "時間区間に未保存の編集があります"
                : "時間区間の保存準備中の変更なし")
            : state.HasStagedChanges
                ? "ローカルで保存準備済み（履歴への記録が必要）"
                : "保存準備中の変更なし";
        string conflicts = state.ContentConflictSegmentIds.Count == 0 &&
                           state.TimelineConflicts.Count == 0
            ? string.Empty
            : $" ／ 競合あり: {string.Join("、", state.ContentConflictSegmentIds)}（編集内容は保持）";
        string prefix = timeLoaded
            ? $"共同編集: {Path.GetFileName(_timeWorkspace!.Directory)} ／ {staged}"
            : $"共同編集: {Path.GetFileName(state.WorkspaceDirectory)} ／ {staged}{conflicts}";
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

    private async Task SafelyAsync(Func<Task> action)
    {
        _notice = null;
        try
        {
            await action();
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
        if (technical.Contains("song file", StringComparison.OrdinalIgnoreCase) ||
            technical.Contains("measured input audio", StringComparison.OrdinalIgnoreCase) ||
            technical.Contains("source audio", StringComparison.OrdinalIgnoreCase))
            return "書き出し先のフォルダーに元の音源が見つからないか、SHA-256が一致しません。同じファイル名・内容の音源を置いてください。";
        if (technical.Contains("remote", StringComparison.OrdinalIgnoreCase) ||
            technical.Contains("origin", StringComparison.OrdinalIgnoreCase) ||
            technical.Contains("bare", StringComparison.OrdinalIgnoreCase))
            return "共有先のGitフォルダーが設定されていない、または利用できません。「詳細設定」でローカル共有フォルダーのパスを確認してください。編集内容は保持されています。";
        if (technical.Contains("uncommitted", StringComparison.OrdinalIgnoreCase) ||
            technical.Contains("dirty", StringComparison.OrdinalIgnoreCase) ||
            technical.Contains("unsaved", StringComparison.OrdinalIgnoreCase))
            return "未保存の編集があるため操作を中止しました。担当区間の変更を保存してから操作してください。";
        // Human-readable diagnostics from the completion preflight must not
        // be hidden behind a generic Segment ownership warning.
        if (technical.Any(c => c >= '\u3040' && c <= '\u9fff'))
            return technical;
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
        string owner = ParticipantNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("参加者一覧から自分の名前を選択してください。");
        return owner;
    }

    private void CreateTimeFirstWorkspace(
        string workspacePath, int sampleRate, string ownerId)
    {
        if (_plannedTimeline is null || _timelineWaveform is null ||
            _musicTimingMap is null || _timelineAudioPath is null)
            throw new InvalidOperationException(
                "時間区間Workspaceには実測済みの音源、音楽グリッド、担当区間が必要です。");
        if (_plannedTimeline.SampleRate != sampleRate ||
            _timelineWaveform.SampleRate != sampleRate)
            throw new InvalidOperationException(
                "確認したサンプル周波数と時間区間の基準が一致しません。");

        var content = _plannedTimeline.Partitions.ToDictionary(
            partition => partition.SegmentId,
            SegmentContentPlanner.CreateEmpty,
            StringComparer.Ordinal);
        var drafts = _plannedTimeline.Partitions.ToDictionary(
            partition => partition.SegmentId,
            partition => SegmentTimeDraftEditor.Open(
                _plannedTimeline, content, partition.SegmentId, partition.OwnerId!),
            StringComparer.Ordinal);
        var snapshot = new TimeFirstSegmentSnapshot(
            new TimeFirstAudioIdentity(
                Path.GetFileName(_timelineAudioPath),
                ComputeSha256(_timelineAudioPath),
                sampleRate,
                _timelineWaveform.TotalSamples),
            _musicTimingMap.TimingPoints.ToArray(),
            _plannedTimeline,
            content,
            drafts);

        TimeFirstSegmentGitWorkspace workspace =
            TimeFirstSegmentGitWorkspace.Initialize(workspacePath);
        workspace.Save(snapshot);
        workspace.Commit("Create time-first Together workspace");
        _timeWorkspace = workspace;
        _timeOwnerId = ownerId;
        _timeSelectedSegmentId = null;
        ApplyTimeFirstSnapshot(workspace.Load());
    }

    private void OpenTimeFirstWorkspace(string workspacePath)
    {
        TimeFirstSegmentGitWorkspace workspace =
            TimeFirstSegmentGitWorkspace.Open(workspacePath);
        TimeFirstSegmentSnapshot snapshot = workspace.Load();
        TimeFirstSegmentGitWorkspace.ValidateSnapshot(snapshot);
        _timeWorkspace = workspace;
        _timeOwnerId = null;
        _timeSelectedSegmentId = null;
        ApplyTimeFirstSnapshot(snapshot);
    }

    private void ApplyTimeFirstSnapshot(TimeFirstSegmentSnapshot snapshot)
    {
        TimeFirstSegmentGitWorkspace.ValidateSnapshot(snapshot);
        _timeSnapshot = snapshot;
        _timeEditor = null;
        _timeSelectedSegmentId = null;
        _timeStaged = false;
        _plannedTimeline = snapshot.Timeline;
        _sectionPlan = null;
        _selectedPlanSectionId = null;
        _musicTimingMap = MusicTimingMap.Build(
            snapshot.Timeline.SampleRate, snapshot.TimingPoints);
        SampleRateBox.Text = snapshot.Timeline.SampleRate.ToString(
            CultureInfo.InvariantCulture);
        ConfirmSampleRateBox.IsChecked = true;
        if (snapshot.TimingPoints.Count > 0)
        {
            BpmBox.Text = snapshot.TimingPoints[0].BeatsPerMinute.ToString(
                CultureInfo.InvariantCulture);
            BeatOffsetBox.Text = (snapshot.TimingPoints[0].StartSample * 1000m /
                snapshot.Timeline.SampleRate).ToString(
                    "0.###", CultureInfo.InvariantCulture);
        }
        if (_timelineWaveform is not null && _timelineAudioPath is not null &&
            _timelineWaveform.SampleRate == snapshot.Audio.SampleRate &&
            _timelineWaveform.TotalSamples == snapshot.Audio.TotalSamples &&
            string.Equals(Path.GetFileName(_timelineAudioPath), snapshot.Audio.FileName,
                StringComparison.Ordinal) &&
            File.Exists(_timelineAudioPath) &&
            string.Equals(ComputeSha256(_timelineAudioPath), snapshot.Audio.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            MusicTimeline.Load(
                _timelineWaveform, _musicTimingMap, snapshot.Timeline.Partitions);
        }
        else
        {
            _timelineWaveform = null;
            _timelineAudioPath = null;
            MusicTimeline.Clear();
            MeasuredAudioText.Text =
                $"保存音源: {snapshot.Audio.FileName} ／ {snapshot.Audio.SampleRate:N0} Hz ／ 再表示には同じ音源を選択";
        }
        _selectionStartSample = null;
        _selectionEndSampleExclusive = null;
        SelectionStartSampleText.Text = "―";
        SelectionEndSampleText.Text = "―";
        RefreshParticipantChoices();
    }

    private TogetherSectionPlan RequiredSectionPlan() =>
        _sectionPlan ?? throw new InvalidOperationException(
            "先に音源を選択してください。曲全体が自動的に1区間になります。");

    private string RequiredPlannedSelection() =>
        _selectedPlanSectionId ?? throw new InvalidOperationException(
            "先に一覧から区間を選択してください。");

    private long PlanningSample()
    {
        if (_timelineWaveform is null || _musicTimingMap is null)
            throw new InvalidOperationException("先に音源を選択してください。");
        long source = _selectionStartSample ?? PreviewSamplePosition();
        long snapped = _musicTimingMap.Snap(
            source, SelectedSubdivision()).AudioSample;
        return Math.Clamp(snapped, 0, _timelineWaveform.TotalSamples);
    }

    private void SplitPlanAt(long sample)
    {
        TogetherSectionPlan plan = RequiredSectionPlan();
        TogetherSection? containing = plan.Sections.FirstOrDefault(section =>
            sample > section.StartSample && sample < section.EndSampleExclusive);
        if (containing is not null)
            _sectionPlan = TogetherSectionPlanner.Split(
                plan, containing.InternalId, sample);
    }

    private void ApplySectionPlan(string notice)
    {
        TogetherSectionPlan plan = RequiredSectionPlan();
        _plannedTimeline = PlanDisplayTimeline(plan);
        MusicTimeline.SetSegments(_plannedTimeline.Partitions);
        _notice = notice;
    }

    private static SegmentTimeTimeline PlanDisplayTimeline(
        TogetherSectionPlan plan) =>
        new(plan.SampleRate, plan.Sections.Select(section =>
            new SegmentTimePartition(
                section.InternalId,
                section.StartSample,
                section.EndSampleExclusive,
                ContentRevisionId: null,
                ContentOriginSample: section.StartSample,
                OwnerId: section.OwnerName,
                Status: SegmentPartitionStatus.Active)).ToArray());

    private void RefreshParticipantChoices()
    {
        IEnumerable<string?> owners = _timeSnapshot is not null
            ? _timeSnapshot.Timeline.Partitions.Select(partition => partition.OwnerId)
            : _sectionPlan?.Sections.Select(section => section.OwnerName)
                ?? Enumerable.Empty<string?>();
        string[] names = owners
            .Where(static owner => !string.IsNullOrWhiteSpace(owner))
            .Select(static owner => owner!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static owner => owner, StringComparer.Ordinal)
            .ToArray();
        string previous = ParticipantNameBox.Text;
        ParticipantNameBox.ItemsSource = names;
        ParticipantNameBox.Text = previous;
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
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

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ExtremeEditor.Core;
using Microsoft.Win32;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private EditorSession? _editor;
    private bool _editorFeaturesInitialized;
    private bool _allowClose;
    private bool _saveBeforeCloseInProgress;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_editorFeaturesInitialized)
            return;

        _editorFeaturesInitialized = true;
        InitializeEditorCommandBindings();
        EnsureEditorSession();
        RefreshInspector();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowClose || _editor is null || !_editor.IsDirty)
        {
            base.OnClosing(e);
            return;
        }

        if (_saveBeforeCloseInProgress)
        {
            e.Cancel = true;
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            this,
            "Save changes before closing?",
            "ExtremeEditor",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (result == MessageBoxResult.Yes)
        {
            e.Cancel = true;
            _saveBeforeCloseInProgress = true;
            _ = SaveAndCloseAsync();
            return;
        }

        _allowClose = true;
        base.OnClosing(e);
    }

    private async Task SaveAndCloseAsync()
    {
        try
        {
            if (!await SaveCurrentAsync(saveAs: false))
                return;
            _allowClose = true;
            Close();
        }
        finally
        {
            _saveBeforeCloseInProgress = false;
        }
    }

    private void InitializeEditorCommandBindings()
    {
        AddBinding(EditorCommands.Save, ExecuteSave, CanExecuteEditorDocument);
        AddBinding(EditorCommands.SaveAs, ExecuteSaveAs, CanExecuteEditorDocument);
        AddBinding(EditorCommands.Undo, ExecuteUndo, CanExecuteUndo);
        AddBinding(EditorCommands.Redo, ExecuteRedo, CanExecuteRedo);
        AddBinding(EditorCommands.Copy, ExecuteCopy, CanExecuteFloorSelection);
        AddBinding(EditorCommands.Cut, ExecuteCut, CanExecuteFloorSelection);
        AddBinding(EditorCommands.Paste, ExecutePaste, CanExecutePaste);
        AddBinding(EditorCommands.Delete, ExecuteDelete, CanExecuteFloorSelection);
        AddBinding(EditorCommands.InsertAngle, ExecuteInsertAngle, CanExecutePrimarySelection);
        AddBinding(EditorCommands.InsertMidspin, ExecuteInsertMidspin, CanExecutePrimarySelection);
        AddBinding(EditorCommands.InsertFullTurn, ExecuteInsertFullTurn, CanExecutePrimarySelection);
        AddBinding(EditorCommands.RotateLeft, (_, e) => ExecuteTransform(e, -90.0), CanExecuteFloorSelection);
        AddBinding(EditorCommands.RotateRight, (_, e) => ExecuteTransform(e, 90.0), CanExecuteFloorSelection);
        AddBinding(EditorCommands.Rotate180, (_, e) => ExecuteTransform(e, 180.0), CanExecuteFloorSelection);
        AddBinding(EditorCommands.FlipHorizontal, ExecuteFlipHorizontal, CanExecuteFloorSelection);
        AddBinding(EditorCommands.FlipVertical, ExecuteFlipVertical, CanExecuteFloorSelection);
        AddBinding(EditorCommands.AddEvent, ExecuteAddEvent, CanExecutePrimarySelection);
        AddBinding(EditorCommands.DeleteEvent, ExecuteDeleteEvent, CanExecuteSelectedEvent);
        AddBinding(EditorCommands.ApplyEvent, ExecuteApplyEvent, CanExecuteSelectedEvent);
    }

    private void AddBinding(
        ICommand command,
        ExecutedRoutedEventHandler executed,
        CanExecuteRoutedEventHandler canExecute)
    {
        CommandBindings.Add(new CommandBinding(command, executed, canExecute));
    }

    private EditorSession? EnsureEditorSession()
    {
        if (_level is null)
            return null;
        if (_editor is not null && ReferenceEquals(_editor.Document, _level))
            return _editor;

        _editor = new EditorSession(_level);
        CommandManager.InvalidateRequerySuggested();
        return _editor;
    }

    private bool ConfirmDiscardCurrentChanges()
    {
        if (_editor is null || !_editor.IsDirty)
            return true;

        MessageBoxResult result = MessageBox.Show(
            this,
            "The current chart has unsaved changes. Discard them?",
            "ExtremeEditor",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }

    private void MainWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or PasswordBox)
            return;
        if (_level is null || _selection.PrimaryFloor < 0)
            return;

        if (e.Key == Key.Delete)
        {
            if (_selection.SelectedFloors.Count > 0)
                EditorCommands.Delete.Execute(null, this);
            e.Handled = true;
            return;
        }

        int target = _selection.PrimaryFloor;
        switch (e.Key)
        {
            case Key.Left:
                target = Math.Max(0, target - 1);
                break;
            case Key.Right:
                target = Math.Min(_level.FloorCount - 1, target + 1);
                break;
            case Key.Home:
                target = 0;
                break;
            case Key.End:
                target = Math.Max(0, _level.FloorCount - 1);
                break;
            default:
                return;
        }

        _selection.MoveSelection(target, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        e.Handled = true;
    }

    private async void ExecuteSave(object sender, ExecutedRoutedEventArgs e)
    {
        await SaveCurrentAsync(saveAs: false);
        e.Handled = true;
    }

    private async void ExecuteSaveAs(object sender, ExecutedRoutedEventArgs e)
    {
        await SaveCurrentAsync(saveAs: true);
        e.Handled = true;
    }

    private async Task<bool> SaveCurrentAsync(bool saveAs)
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null)
            return false;

        string? path = _level?.SourcePath;
        if (saveAs || string.IsNullOrWhiteSpace(path) || path == "<synthetic>")
        {
            var dialog = new SaveFileDialog
            {
                Filter = "ADOFAI levels (*.adofai)|*.adofai|All files (*.*)|*.*",
                Title = "Save ADOFAI level",
                FileName = path is null or "<synthetic>" ? "level.adofai" : Path.GetFileName(path)
            };
            if (dialog.ShowDialog(this) != true)
                return false;
            path = dialog.FileName;
        }

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            StatusText.Text = "Saving…";
            await editor.SaveAsync(path!);
            Title = $"ExtremeEditor {EditorVersion.Current} — WPF — {Path.GetFileName(path)}";
            StatusText.Text = $"Saved {Path.GetFileName(path)}";
            CommandManager.InvalidateRequerySuggested();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Save failed";
            return false;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void ExecuteUndo(object sender, ExecutedRoutedEventArgs e)
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null)
            return;
        int primary = _selection.PrimaryFloor;
        editor.Undo();
        RefreshEditorAfterMutation(primary);
        e.Handled = true;
    }

    private void ExecuteRedo(object sender, ExecutedRoutedEventArgs e)
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null)
            return;
        int primary = _selection.PrimaryFloor;
        editor.Redo();
        RefreshEditorAfterMutation(primary);
        e.Handled = true;
    }

    private void ExecuteCopy(object sender, ExecutedRoutedEventArgs e)
    {
        EditorSession? editor = EnsureEditorSession();
        editor?.CopyFloors(_selection.SelectedFloors);
        StatusText.Text = $"Copied {_selection.SelectedFloors.Count:N0} floor(s)";
        CommandManager.InvalidateRequerySuggested();
        e.Handled = true;
    }

    private void ExecuteCut(object sender, ExecutedRoutedEventArgs e)
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null)
            return;
        int target = Math.Max(0, _selection.SelectedFloors.DefaultIfEmpty(1).Min() - 1);
        editor.CutFloors(_selection.SelectedFloors);
        RefreshEditorAfterMutation(target);
        e.Handled = true;
    }

    private void ExecutePaste(object sender, ExecutedRoutedEventArgs e)
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null || _selection.PrimaryFloor < 0)
            return;
        int newFloor = _selection.PrimaryFloor + 1;
        editor.PasteFloors(_selection.PrimaryFloor);
        RefreshEditorAfterMutation(newFloor);
        e.Handled = true;
    }

    private void ExecuteDelete(object sender, ExecutedRoutedEventArgs e)
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null)
            return;
        int target = Math.Max(0, _selection.SelectedFloors.DefaultIfEmpty(1).Min() - 1);
        editor.DeleteFloors(_selection.SelectedFloors);
        RefreshEditorAfterMutation(target);
        e.Handled = true;
    }

    private void ExecuteInsertAngle(object sender, ExecutedRoutedEventArgs e)
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null || _selection.PrimaryFloor < 0)
            return;
        double? angle = InputDialog.AskDouble(this, "Insert floor", "Absolute ADOFAI angle in degrees:", 0.0);
        if (angle is null)
            return;
        int newFloor = _selection.PrimaryFloor + 1;
        editor.InsertAngle(_selection.PrimaryFloor, angle.Value);
        RefreshEditorAfterMutation(newFloor);
        e.Handled = true;
    }

    private void ExecuteInsertMidspin(object sender, ExecutedRoutedEventArgs e)
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null || _selection.PrimaryFloor < 0)
            return;
        int newFloor = _selection.PrimaryFloor + 1;
        editor.InsertMidspin(_selection.PrimaryFloor);
        RefreshEditorAfterMutation(newFloor);
        e.Handled = true;
    }

    private void ExecuteInsertFullTurn(object sender, ExecutedRoutedEventArgs e)
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null || _selection.PrimaryFloor < 0)
            return;
        int newFloor = _selection.PrimaryFloor + 1;
        editor.InsertFullTurn(_selection.PrimaryFloor);
        RefreshEditorAfterMutation(newFloor);
        e.Handled = true;
    }

    private void ExecuteTransform(ExecutedRoutedEventArgs e, double degrees)
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null)
            return;
        int[] selection = _selection.SelectedFloors.ToArray();
        int primary = _selection.PrimaryFloor;
        editor.Rotate(selection, degrees);
        RefreshEditorAfterMutation(primary, selection);
        e.Handled = true;
    }

    private void ExecuteFlipHorizontal(object sender, ExecutedRoutedEventArgs e)
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null)
            return;
        int[] selection = _selection.SelectedFloors.ToArray();
        int primary = _selection.PrimaryFloor;
        editor.FlipHorizontal(selection);
        RefreshEditorAfterMutation(primary, selection);
        e.Handled = true;
    }

    private void ExecuteFlipVertical(object sender, ExecutedRoutedEventArgs e)
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null)
            return;
        int[] selection = _selection.SelectedFloors.ToArray();
        int primary = _selection.PrimaryFloor;
        editor.FlipVertical(selection);
        RefreshEditorAfterMutation(primary, selection);
        e.Handled = true;
    }

    private void ExecuteAddEvent(object sender, ExecutedRoutedEventArgs e)
    {
        EditorSession? editor = EnsureEditorSession();
        if (editor is null || _selection.PrimaryFloor < 0)
            return;
        string? type = InputDialog.Ask(this, "Add event", "eventType:", "Twirl");
        if (string.IsNullOrWhiteSpace(type))
            return;
        int[] selection = _selection.SelectedFloors.ToArray();
        int primary = _selection.PrimaryFloor;
        editor.AddAction(primary, type);
        RefreshEditorAfterMutation(primary, selection);
        e.Handled = true;
    }

    private void ExecuteDeleteEvent(object sender, ExecutedRoutedEventArgs e)
    {
        if (EventList.SelectedItem is not EventListItem item)
            return;
        EditorSession? editor = EnsureEditorSession();
        if (editor is null)
            return;
        int selectedIndex = EventList.SelectedIndex;
        int[] selection = _selection.SelectedFloors.ToArray();
        int primary = _selection.PrimaryFloor;
        editor.DeleteAction(item.Action);
        RefreshEditorAfterMutation(primary, selection);
        if (EventList.Items.Count > 0)
            EventList.SelectedIndex = Math.Clamp(selectedIndex, 0, EventList.Items.Count - 1);
        e.Handled = true;
    }

    private void ExecuteApplyEvent(object sender, ExecutedRoutedEventArgs e)
    {
        if (EventList.SelectedItem is not EventListItem item)
            return;
        EditorSession? editor = EnsureEditorSession();
        if (editor is null)
            return;

        try
        {
            JsonObject obj = JsonNode.Parse(EventEditorText.Text) as JsonObject
                             ?? throw new JsonException("Event editor expects a JSON object.");
            LevelAction updated = ParseEditedAction(obj, item.Action);
            updated.PropertyOverrides = (JsonObject)obj.DeepClone();
            updated.PropertyOverridesStructureRevision = editor.StructureEdits.Count;
            int[] selection = _selection.SelectedFloors.ToArray();
            editor.ReplaceAction(item.Action, updated);
            RefreshEditorAfterMutation(updated.Floor, selection);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Invalid event JSON", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        e.Handled = true;
    }

    private void EventListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EventList.SelectedItem is EventListItem item && EnsureEditorSession() is EditorSession editor)
        {
            JsonObject obj = AdoFaiEditorSaveService.BuildEditableActionJson(editor, item.Action);
            EventEditorText.Text = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        else
        {
            EventEditorText.Clear();
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshEditorAfterMutation(int preferredPrimary, IEnumerable<int>? preferredSelection = null)
    {
        if (_level is null)
            return;

        StopPlayback();
        int[] selection = preferredSelection?.ToArray() ?? [preferredPrimary];
        int maxFloor = Math.Max(0, _level.FloorCount - 1);
        selection = selection.Select(floor => Math.Clamp(floor, 0, maxFloor)).Distinct().ToArray();
        int primary = Math.Clamp(preferredPrimary, 0, maxFloor);

        NativeViewport.SetLevel(_level);
        _selection.SetFloorCount(_level.FloorCount);
        _selection.SetSelection(selection, primary);

        WpfPlaybackSetup playback = WpfPlaybackSetupBuilder.Build(_level);
        _timingMap = playback.TimingMap;
        _hitSoundTimeline = playback.HitSoundTimeline;
        _audio.ConfigureHitSounds(_level, _timingMap, _hitSoundTimeline);
        NativeViewport.SetPlaybackTimeline(_timingMap);

        RefreshInspector();
        UpdateEditorStatus();
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshInspector()
    {
        EditorSession? editor = EnsureEditorSession();
        int primary = _selection.PrimaryFloor;
        int count = _selection.SelectedFloors.Count;
        SelectionSummaryText.Text = primary < 0
            ? "No floor selected"
            : count <= 1
                ? $"Floor {primary:N0}"
                : $"{count:N0} floors selected | primary {primary:N0}";

        int? selectedSource = (EventList.SelectedItem as EventListItem)?.Action.SourceIndex;
        IReadOnlyList<LevelAction> actions = editor is null || primary < 0
            ? Array.Empty<LevelAction>()
            : editor.GetActionsAtFloor(primary);
        EventList.ItemsSource = actions.Select(action => new EventListItem(action)).ToArray();
        if (selectedSource is int source)
        {
            EventList.SelectedItem = EventList.Items
                .Cast<EventListItem>()
                .FirstOrDefault(item => item.Action.SourceIndex == source);
        }
        if (EventList.SelectedItem is null)
            EventEditorText.Clear();
    }

    private void UpdateEditorStatus()
    {
        if (_editor is null || _level is null)
            return;
        string dirty = _editor.IsDirty ? "modified" : "saved";
        StatusText.Text = $"{Path.GetFileName(_level.SourcePath)} | floors {_level.FloorCount:N0} | actions {_level.ActionCount:N0} | {dirty}";
        Title = $"ExtremeEditor {EditorVersion.Current} — WPF{(_editor.IsDirty ? " *" : string.Empty)}";
    }

    private static LevelAction ParseEditedAction(JsonObject obj, LevelAction original)
    {
        int floor = GetInt(obj, "floor", original.Floor);
        string eventType = GetString(obj, "eventType") ?? original.EventType;
        bool active = GetBool(obj, "active", original.Active);
        return new LevelAction(
            floor,
            eventType,
            active,
            GetString(obj, "speedType"),
            GetDouble(obj, "beatsPerMinute"),
            GetDouble(obj, "bpmMultiplier"),
            GetString(obj, "icon"))
        {
            SourceIndex = original.SourceIndex,
            Kind = LevelActionKinds.FromEventType(eventType),
            HitSound = GetString(obj, "hitsound"),
            HitSoundVolumePercent = GetDouble(obj, "hitsoundVolume"),
            GameSound = GetString(obj, "gameSound"),
            Planets = GetString(obj, "planets"),
            AngleOffset = GetDouble(obj, "angleOffset"),
            Duration = GetDouble(obj, "duration"),
            PropertyOverrides = original.PropertyOverrides,
            PropertyOverridesStructureRevision = original.PropertyOverridesStructureRevision
        };
    }

    private static int GetInt(JsonObject obj, string name, int fallback)
    {
        return obj[name] is JsonValue value && value.TryGetValue(out int result) ? result : fallback;
    }

    private static string? GetString(JsonObject obj, string name)
    {
        return obj[name] is JsonValue value && value.TryGetValue(out string? result) ? result : null;
    }

    private static double? GetDouble(JsonObject obj, string name)
    {
        if (obj[name] is not JsonValue value)
            return null;
        if (value.TryGetValue(out double result))
            return result;
        return value.TryGetValue(out string? text) && double.TryParse(text, out result) ? result : null;
    }

    private static bool GetBool(JsonObject obj, string name, bool fallback)
    {
        return obj[name] is JsonValue value && value.TryGetValue(out bool result) ? result : fallback;
    }

    private void CanExecuteEditorDocument(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = !_isLoading && EnsureEditorSession() is not null;
        e.Handled = true;
    }

    private void CanExecuteUndo(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = !_isLoading && EnsureEditorSession()?.CanUndo == true;
        e.Handled = true;
    }

    private void CanExecuteRedo(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = !_isLoading && EnsureEditorSession()?.CanRedo == true;
        e.Handled = true;
    }

    private void CanExecuteFloorSelection(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = !_isLoading && EnsureEditorSession() is not null && _selection.SelectedFloors.Count > 0;
        e.Handled = true;
    }

    private void CanExecutePrimarySelection(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = !_isLoading && EnsureEditorSession() is not null && _selection.PrimaryFloor >= 0;
        e.Handled = true;
    }

    private void CanExecutePaste(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = !_isLoading && EnsureEditorSession()?.HasClipboard == true && _selection.PrimaryFloor >= 0;
        e.Handled = true;
    }

    private void CanExecuteSelectedEvent(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = !_isLoading && EnsureEditorSession() is not null && EventList.SelectedItem is EventListItem;
        e.Handled = true;
    }

    private sealed record EventListItem(LevelAction Action)
    {
        public override string ToString() => $"{Action.EventType} @ {Action.Floor:N0}";
    }
}

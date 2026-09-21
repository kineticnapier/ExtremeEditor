using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private bool _eventAutoApplyInProgress;

    private static void RegisterEventPropertyAutoApplyHandlers()
    {
        EventManager.RegisterClassHandler(
            typeof(StackPanel),
            Keyboard.LostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(EventPropertyPanelLostKeyboardFocus),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(StackPanel),
            Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler(EventPropertyPanelSelectionChanged),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(StackPanel),
            ToggleButton.CheckedEvent,
            new RoutedEventHandler(EventPropertyPanelToggleChanged),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(StackPanel),
            ToggleButton.UncheckedEvent,
            new RoutedEventHandler(EventPropertyPanelToggleChanged),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(StackPanel),
            Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler(EventPropertyPanelPreviewKeyDown),
            handledEventsToo: true);
    }

    private static MainWindow? GetEventPropertyWindow(object sender)
    {
        if (sender is not StackPanel panel || Window.GetWindow(panel) is not MainWindow window)
            return null;
        return ReferenceEquals(panel, window._eventPropertyPanel) ? window : null;
    }

    private static void EventPropertyPanelLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        MainWindow? window = GetEventPropertyWindow(sender);
        if (window is null || ReferenceEquals(e.OriginalSource, window.EventEditorText))
            return;
        window.CommitEventPropertyDraftFromUi();
    }

    private static void EventPropertyPanelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        GetEventPropertyWindow(sender)?.CommitEventPropertyDraftFromUi();
    }

    private static void EventPropertyPanelToggleChanged(object sender, RoutedEventArgs e)
    {
        GetEventPropertyWindow(sender)?.CommitEventPropertyDraftFromUi();
    }

    private static void EventPropertyPanelPreviewKeyDown(object sender, KeyEventArgs e)
    {
        MainWindow? window = GetEventPropertyWindow(sender);
        if (window is null || e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
            return;
        if (e.OriginalSource is not TextBox box || ReferenceEquals(box, window.EventEditorText))
            return;

        if (!box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)))
            window.EventList.Focus();
        e.Handled = true;
    }

    private void CommitEventPropertyDraftFromUi()
    {
        if (_eventAutoApplyInProgress || _eventPropertyDraft is null)
            return;
        if (EventList.SelectedItem is not EventListItem item)
            return;

        EditorSession? editor = EnsureEditorSession();
        if (editor is null)
            return;

        LevelAction before = editor.GetActionsAtFloor(item.Action.Floor)
            .FirstOrDefault(action => action.SourceIndex == item.Action.SourceIndex)
            ?? item.Action;

        JsonObject draft = (JsonObject)_eventPropertyDraft.DeepClone();
        JsonObject current = AdoFaiEditorSaveService.BuildEditableActionJson(editor, before);
        if (JsonNode.DeepEquals(current, draft))
            return;

        _eventAutoApplyInProgress = true;
        try
        {
            LevelAction updated = ParseEditedAction(draft, before);
            updated.PropertyOverrides = (JsonObject)draft.DeepClone();
            updated.PropertyOverridesStructureRevision = editor.StructureEdits.Count;
            editor.ReplaceAction(before, updated);
            RefreshAfterEventPropertyCommit(before, updated);
        }
        finally
        {
            _eventAutoApplyInProgress = false;
        }
    }

    private void RefreshAfterEventPropertyCommit(LevelAction before, LevelAction after)
    {
        if (_level is null)
            return;

        StopPlayback();

        bool timingChanged = AffectsEditorTiming(before) || AffectsEditorTiming(after);
        bool hitSoundChanged = timingChanged ||
                               before.Kind == LevelActionKind.SetHitsound ||
                               after.Kind == LevelActionKind.SetHitsound;
        bool speedVisualChanged = before.Kind == LevelActionKind.SetSpeed ||
                                  after.Kind == LevelActionKind.SetSpeed;

        if (speedVisualChanged)
            RecomputeSpeedRatios();

        if (timingChanged)
        {
            _timingMap = FlatTimingMapBuilder.Build(_level);
            NativeViewport.SetPlaybackTimeline(_timingMap);
        }

        if (speedVisualChanged)
            NativeViewport.SetLevel(_level);

        if (hitSoundChanged)
        {
            _timingMap ??= FlatTimingMapBuilder.Build(_level);
            _hitSoundTimeline = HitSoundTimelineBuilder.Build(_level);
            _audio.ConfigureHitSounds(_level, _timingMap, _hitSoundTimeline);
        }

        UpdateEditorStatus();
        CommandManager.InvalidateRequerySuggested();
    }

    private void RecomputeSpeedRatios()
    {
        if (_level is null)
            return;

        double bpm = _level.InitialBpm > 0 ? _level.InitialBpm : 100.0;
        LevelActionStore store = _level.ActionStore;
        for (int actionFloorIndex = 0; actionFloorIndex < store.ActionFloorCount; actionFloorIndex++)
        {
            ReadOnlySpan<LevelAction> actions = store.GetActionsAt(actionFloorIndex);
            foreach (LevelAction action in actions)
            {
                if (action.Kind != LevelActionKind.SetSpeed)
                    continue;

                action.SpeedRatio = null;
                if (!action.Active)
                    continue;

                if (string.Equals(action.SpeedType, "Multiplier", StringComparison.OrdinalIgnoreCase) &&
                    action.BpmMultiplier is double multiplier && multiplier > 0)
                {
                    action.SpeedRatio = multiplier;
                    bpm *= multiplier;
                }
                else if (action.BeatsPerMinute is double target && target > 0)
                {
                    action.SpeedRatio = bpm > 0 ? target / bpm : null;
                    bpm = target;
                }
            }
        }
    }

    private static bool AffectsEditorTiming(LevelAction action) => action.Kind is
        LevelActionKind.Twirl or
        LevelActionKind.MultiPlanet or
        LevelActionKind.SetSpeed or
        LevelActionKind.Pause;
}

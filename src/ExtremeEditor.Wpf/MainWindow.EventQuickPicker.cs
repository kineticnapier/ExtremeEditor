using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private static readonly HashSet<string> DecorationObjectEventTypes = new(StringComparer.Ordinal)
    {
        "AddDecoration",
        "AddText",
        "AddObject",
        "AddParticle"
    };

    private static readonly EventCategoryDefinition[] EventCategories =
    [
        new("Gameplay",
        [
            "SetSpeed", "Twirl", "Multitap", "Checkpoint", "SetHitsound",
            "SetPlanetRotation", "AutoPlayTiles", "Pause", "KillPlayer", "PlaySound", "ScalePlanets"
        ]),
        new("TrackFx",
        [
            "ColorTrack", "AnimateTrack", "RecolorTrack", "MoveTrack", "PositionTrack",
            "TileDimensions", "SetFloorIcon"
        ]),
        new("DecorationFx",
        [
            "AddDecoration", "AddText", "MoveDecorations", "SetText", "AddObject",
            "SetObject", "SetDefaultText", "SetParticle", "EmitParticle"
        ]),
        new("VisualFx",
        [
            "CustomBackground", "Flash", "MoveCamera", "SetFilter", "SetFilterAdvanced",
            "HallOfMirrors", "ShakeScreen", "Bloom", "ScreenTile", "ScreenScroll", "SetFrameRate"
        ]),
        new("FxModifiers",
        [
            "RepeatEvents", "SetConditionalEvents", "SetInputEvent"
        ]),
        new("Jank",
        [
            "Hold", "SetHoldSound", "MultiPlanet", "ScaleMargin", "ScaleRadius",
            "FreeRoam", "FreeRoamTwirl", "FreeRoamRemove", "FreeRoamWarning", "Hide"
        ]),
        new("Conveniences",
        [
            "EditorComment", "Bookmark", "CallMethod", "AddComponent"
        ]),
        new("Favorites", [])
    ];

    private static readonly EventCatalogEntry[] EventCatalog = EventCategories
        .SelectMany(category => category.Events.Select(eventType => new EventCatalogEntry(category.Name, eventType)))
        .Where(entry => !DecorationObjectEventTypes.Contains(entry.EventType))
        .ToArray();

    private int _eventCategoryIndex;
    private TextBlock? _eventQuickPickerText;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += EventQuickPickerLoaded;
    }

    private void EventQuickPickerLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= EventQuickPickerLoaded;

        // MainWindow.xaml wires the legacy handler directly. Replace it once the
        // visual tree is live so transport/event shortcuts run before its text-box guard.
        PreviewKeyDown -= AdoFaiPreviewKeyDown;
        PreviewKeyDown += ExtremeEditorPreviewKeyDown;

        if (FindDescendantButton(this, "Add Event") is Button addEventButton)
        {
            addEventButton.Command = null;
            addEventButton.Content = "Add Event…";
            addEventButton.Click += OpenEventPicker;

            if (addEventButton.Parent is StackPanel buttonRow &&
                buttonRow.Parent is StackPanel inspectorHeader)
            {
                _eventQuickPickerText = new TextBlock
                {
                    Foreground = System.Windows.Media.Brushes.Gray,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 10,
                    Margin = new Thickness(0, 0, 0, 8),
                    TextWrapping = TextWrapping.Wrap
                };
                int insertIndex = inspectorHeader.Children.IndexOf(buttonRow) + 1;
                inspectorHeader.Children.Insert(insertIndex, _eventQuickPickerText);
            }
        }

        RefreshEventQuickPickerText();
    }

    private static Button? FindDescendantButton(DependencyObject parent, string content)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is Button button && string.Equals(button.Content?.ToString(), content, StringComparison.Ordinal))
                return button;
            if (FindDescendantButton(child, content) is Button nested)
                return nested;
        }
        return null;
    }

    private void ExtremeEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys modifiers = Keyboard.Modifiers;
        bool control = (modifiers & ModifierKeys.Control) != 0;
        bool shift = (modifiers & ModifierKeys.Shift) != 0;
        bool alt = (modifiers & ModifierKeys.Alt) != 0;
        bool windows = (modifiers & ModifierKeys.Windows) != 0;

        // ADOFAI leaves edit mode when Escape is pressed during editor playback.
        // Handle this before the text-box guard in AdoFaiPreviewKeyDown so Escape
        // still stops playback while the raw event editor has keyboard focus.
        if (key == Key.Escape && !control && !shift && !alt && !windows && IsEditorPlaybackTransportActive())
        {
            StopPlayback();
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is not TextBox and not PasswordBox && !e.IsRepeat)
        {
            if (!shift && !alt && !windows && TryGetTopRowDigit(key, out int digit))
            {
                if (control)
                {
                    if ((uint)digit < (uint)EventCategories.Length)
                    {
                        _eventCategoryIndex = digit;
                        RefreshEventQuickPickerText();
                    }
                    e.Handled = true;
                    return;
                }

                if (TryAddNumberedEvent(digit))
                {
                    e.Handled = true;
                    return;
                }
            }

            // ADOFAI names these actions Previous/Next Event Page, but the current
            // implementation cycles LevelEventCategory values. Mirror that behavior.
            if (!control && !alt && !windows && key is Key.OemOpenBrackets or Key.Oem6)
            {
                if (shift)
                    _eventCategoryIndex = key == Key.OemOpenBrackets ? 0 : EventCategories.Length - 1;
                else
                    CycleEventCategory(key == Key.Oem6 ? 1 : -1);
                RefreshEventQuickPickerText();
                e.Handled = true;
                return;
            }
        }

        AdoFaiPreviewKeyDown(sender, e);
    }

    private bool IsEditorPlaybackTransportActive() =>
        _silentPlaybackActive || (_audio.IsLoaded && !_audio.IsStopped);

    private static bool TryGetTopRowDigit(Key key, out int digit)
    {
        if (key >= Key.D0 && key <= Key.D9)
        {
            digit = (int)key - (int)Key.D0;
            return true;
        }

        digit = -1;
        return false;
    }

    private bool TryAddNumberedEvent(int digit)
    {
        if (_level is null || Viewport.SelectedFloor < 0)
            return false;

        EventCategoryDefinition category = EventCategories[_eventCategoryIndex];

        // ADOFAI registers Alpha0..Alpha9. Its event buttons are numbered from 1,
        // so the top-row 0 key is the tenth keyboard-accessible slot.
        int slot = digit == 0 ? 9 : digit - 1;
        if ((uint)slot >= (uint)category.Events.Length)
            return false;

        return TryAddEventAtSelection(category.Events[slot]);
    }

    private void CycleEventCategory(int delta)
    {
        int count = EventCategories.Length;
        _eventCategoryIndex = ((_eventCategoryIndex + delta) % count + count) % count;
    }

    private void RefreshEventQuickPickerText()
    {
        if (_eventQuickPickerText is null)
            return;

        EventCategoryDefinition category = EventCategories[_eventCategoryIndex];
        string keys = string.Join(
            "   ",
            category.Events.Take(10).Select((eventType, index) =>
            {
                string marker = DecorationObjectEventTypes.Contains(eventType) ? "*" : string.Empty;
                return $"{(index == 9 ? 0 : index + 1)} {eventType}{marker}";
            }));
        string note = category.Events.Any(DecorationObjectEventTypes.Contains)
            ? "   * decoration creation pending"
            : string.Empty;
        _eventQuickPickerText.Text = $"Ctrl+{_eventCategoryIndex}: {category.Name}   [ / ] category{note}\n{keys}";
    }

    private void OpenEventPicker(object sender, RoutedEventArgs e)
    {
        if (_level is null || Viewport.SelectedFloor < 0)
            return;

        var dialog = new EventPickerWindow(EventCatalog)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true && dialog.SelectedEvent is EventCatalogEntry selected)
        {
            int categoryIndex = Array.FindIndex(
                EventCategories,
                category => string.Equals(category.Name, selected.Category, StringComparison.Ordinal));
            if (categoryIndex >= 0)
                _eventCategoryIndex = categoryIndex;
            RefreshEventQuickPickerText();
            TryAddEventAtSelection(selected.EventType);
        }
    }

    private bool TryAddEventAtSelection(string eventType)
    {
        if (DecorationObjectEventTypes.Contains(eventType))
        {
            StatusText.Text = $"{eventType} is stored in decorations[]; creation is not wired yet";
            return true;
        }

        EditorSession? editor = EnsureEditorSession();
        int primary = Viewport.SelectedFloor;
        if (editor is null || primary < 0)
            return false;

        int[] selection = Viewport.SelectedFloors.ToArray();
        editor.AddAction(primary, eventType);
        RefreshEditorAfterMutation(primary, selection);

        EventList.SelectedItem = EventList.Items
            .Cast<EventListItem>()
            .LastOrDefault(item => string.Equals(item.Action.EventType, eventType, StringComparison.Ordinal));
        StatusText.Text = $"Added {eventType} @ {primary:N0}";
        return true;
    }

    private sealed record EventCategoryDefinition(string Name, string[] Events);

    private sealed record EventCatalogEntry(string Category, string EventType)
    {
        public override string ToString() => $"{EventType}    [{Category}]";
    }

    private sealed class EventPickerWindow : Window
    {
        private readonly EventCatalogEntry[] _all;
        private readonly TextBox _search;
        private readonly ListBox _list;

        public EventCatalogEntry? SelectedEvent { get; private set; }

        public EventPickerWindow(EventCatalogEntry[] events)
        {
            _all = events;
            Title = "Add Event";
            Width = 500;
            Height = 620;
            MinWidth = 380;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _search = new TextBox
            {
                MinHeight = 30,
                ToolTip = "Search event type or category"
            };
            _search.TextChanged += (_, _) => RefreshFilter();
            _search.PreviewKeyDown += SearchPreviewKeyDown;
            Grid.SetRow(_search, 0);
            root.Children.Add(_search);

            _list = new ListBox();
            _list.MouseDoubleClick += (_, _) => AcceptSelection();
            _list.PreviewKeyDown += ListPreviewKeyDown;
            Grid.SetRow(_list, 2);
            root.Children.Add(_list);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var cancel = new Button { Content = "Cancel", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
            cancel.Click += (_, _) => DialogResult = false;
            var add = new Button { Content = "Add", MinWidth = 80, IsDefault = true };
            add.Click += (_, _) => AcceptSelection();
            buttons.Children.Add(cancel);
            buttons.Children.Add(add);
            Grid.SetRow(buttons, 4);
            root.Children.Add(buttons);

            Content = root;
            RefreshFilter();
            ContentRendered += (_, _) => _search.Focus();
        }

        private void RefreshFilter()
        {
            string query = _search.Text.Trim();
            EventCatalogEntry[] filtered = string.IsNullOrEmpty(query)
                ? _all
                : _all.Where(item =>
                        item.EventType.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        item.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            _list.ItemsSource = filtered;
            if (filtered.Length > 0)
                _list.SelectedIndex = 0;
        }

        private void SearchPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && _list.Items.Count > 0)
            {
                _list.Focus();
                _list.SelectedIndex = Math.Max(0, _list.SelectedIndex);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                AcceptSelection();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
            }
        }

        private void ListPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AcceptSelection();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
            }
        }

        private void AcceptSelection()
        {
            if (_list.SelectedItem is not EventCatalogEntry selected)
                return;
            SelectedEvent = selected;
            DialogResult = true;
        }
    }
}

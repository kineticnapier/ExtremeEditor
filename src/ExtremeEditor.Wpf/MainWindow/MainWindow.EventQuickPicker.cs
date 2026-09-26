using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ExtremeEditor.Rendering;

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
        new("Gameplay", "Gameplay", "◉",
        [
            "SetSpeed", "Twirl", "Multitap", "Checkpoint", "SetHitsound", "PlaySound",
            "SetPlanetRotation", "KillPlayer", "Pause", "AutoPlayTiles", "ScalePlanets"
        ]),
        new("TrackFx", "TrackFx", "▦",
        [
            "ColorTrack", "AnimateTrack", "RecolorTrack", "MoveTrack", "PositionTrack",
            "TileDimensions", "SetFloorIcon"
        ]),
        new("DecorationFx", "DecorationFx", "◆",
        [
            "MoveDecorations", "SetText", "EmitParticle", "SetParticle", "SetObject", "SetDefaultText"
        ]),
        new("VisualFx", "VisualFx", "◫",
        [
            "CustomBackground", "Flash", "MoveCamera", "SetFilter", "SetFilterAdvanced",
            "HallOfMirrors", "ShakeScreen", "Bloom", "ScreenTile", "ScreenScroll", "SetFrameRate"
        ]),
        new("FxModifiers", "FxModifiers", "⚙",
        [
            "RepeatEvents", "SetConditionalEvents", "SetInputEvent"
        ]),
        new("Conveniences", "Conveniences", "★",
        [
            "EditorComment", "Bookmark", "CallMethod", "AddComponent"
        ]),
        new("Jank", "Jank", "+",
        [
            "Hold", "SetHoldSound", "MultiPlanet", "FreeRoam", "FreeRoamTwirl",
            "FreeRoamRemove", "Hide", "ScaleMargin", "ScaleRadius"
        ]),
        new("Favorites", "Favorites", "☆", [])
    ];

    private static readonly EventCatalogEntry[] EventCatalog = EventCategories
        .SelectMany(category => category.Events.Select(eventType => new EventCatalogEntry(category.Name, eventType)))
        .Where(entry => !DecorationObjectEventTypes.Contains(entry.EventType))
        .ToArray();

    private static readonly Brush PaletteSelected = new SolidColorBrush(Color.FromRgb(82, 58, 121));
    private static readonly Brush PaletteNormal = new SolidColorBrush(Color.FromRgb(43, 47, 54));
    private static readonly Brush PaletteBorder = new SolidColorBrush(Color.FromRgb(72, 77, 87));
    private static readonly Brush PaletteText = new SolidColorBrush(Color.FromRgb(238, 240, 244));
    private static readonly Brush PaletteMuted = new SolidColorBrush(Color.FromRgb(166, 172, 184));

    private int _eventCategoryIndex;
    private int _eventPageIndex;
    private StackPanel? _eventCategoryBar;
    private WrapPanel? _eventSlotBar;
    private TextBlock? _eventPageText;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += EventQuickPickerLoaded;
    }

    private void EventQuickPickerLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= EventQuickPickerLoaded;

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
                var palette = BuildEventPalette();
                int insertIndex = inspectorHeader.Children.IndexOf(buttonRow) + 1;
                inspectorHeader.Children.Insert(insertIndex, palette);
            }
        }

        InitializeEventPropertyEditor();
        RefreshEventPalette();
    }

    private FrameworkElement BuildEventPalette()
    {
        var root = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 8)
        };

        _eventCategoryBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 4)
        };
        root.Children.Add(_eventCategoryBar);

        _eventSlotBar = new WrapPanel
        {
            Orientation = Orientation.Horizontal
        };
        root.Children.Add(_eventSlotBar);

        _eventPageText = new TextBlock
        {
            Foreground = PaletteMuted,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            Margin = new Thickness(1, 3, 0, 0)
        };
        root.Children.Add(_eventPageText);

        return root;
    }

    private void RefreshEventPalette()
    {
        if (_eventCategoryBar is null || _eventSlotBar is null || _eventPageText is null)
            return;

        _eventCategoryBar.Children.Clear();
        for (int i = 0; i < EventCategories.Length; i++)
        {
            int categoryIndex = i;
            EventCategoryDefinition category = EventCategories[i];
            var button = new Button
            {
                Width = 35,
                Height = 31,
                Margin = new Thickness(1),
                Padding = new Thickness(0),
                Content = CreateEventCategoryContent(category.AssetKey, category.Glyph),
                FontFamily = new FontFamily("Segoe UI Symbol"),
                FontSize = 16,
                Foreground = PaletteText,
                Background = i == _eventCategoryIndex ? PaletteSelected : PaletteNormal,
                BorderBrush = PaletteBorder,
                BorderThickness = new Thickness(1),
                ToolTip = $"Ctrl+{i}: {category.Name}"
            };
            button.Click += (_, _) =>
            {
                _eventCategoryIndex = categoryIndex;
                _eventPageIndex = 0;
                RefreshEventPalette();
            };
            _eventCategoryBar.Children.Add(button);
        }

        EventCategoryDefinition selectedCategory = GetVisibleEventCategory(EventCategories[_eventCategoryIndex]);
        int pageCount = Math.Max(1, (selectedCategory.Events.Length + 9) / 10);
        _eventPageIndex = Math.Clamp(_eventPageIndex, 0, pageCount - 1);
        int pageStart = _eventPageIndex * 10;

        _eventSlotBar.Children.Clear();
        for (int slot = 0; slot < 10; slot++)
        {
            int eventIndex = pageStart + slot;
            if (eventIndex >= selectedCategory.Events.Length)
                break;

            string eventType = selectedCategory.Events[eventIndex];
            int digit = slot == 9 ? 0 : slot + 1;
            bool decorationPending = DecorationObjectEventTypes.Contains(eventType);

            var button = new Button
            {
                Width = 36,
                Height = 38,
                Margin = new Thickness(1),
                Padding = new Thickness(3),
                Background = PaletteNormal,
                BorderBrush = PaletteBorder,
                BorderThickness = new Thickness(1),
                Content = CreateEventContent(
                    eventType,
                    decorationPending ? PaletteMuted : PaletteText),
                ToolTip = decorationPending
                    ? $"{digit}: {eventType} (decoration creation pending)"
                    : $"{digit}: {eventType}"
            };
            button.Click += (_, _) => TryAddEventAtSelection(eventType);
            _eventSlotBar.Children.Add(button);
        }

        _eventPageText.Text = pageCount > 1
            ? $"{selectedCategory.Name}  page {_eventPageIndex + 1}/{pageCount}    [ / ] page"
            : selectedCategory.Name;
    }

    private static string ShortEventName(string eventType)
    {
        string capitals = new(eventType.Where(char.IsUpper).Take(3).ToArray());
        if (capitals.Length >= 2)
            return capitals;
        return eventType.Length <= 3 ? eventType : eventType[..3];
    }

    private static object CreateEventContent(string eventType, Brush fallbackForeground)
    {
        ImageSource? source = WpfIconRenderer.GetCachedBitmap(IconAssetCache.EventPath(eventType));
        if (source is null)
        {
            return new TextBlock
            {
                Text = ShortEventName(eventType),
                Foreground = fallbackForeground,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        return new Image
        {
            Source = source,
            Width = 24,
            Height = 24,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
    }

    private static Button? FindDescendantButton(DependencyObject parent, string content)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
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
                        _eventPageIndex = 0;
                        RefreshEventPalette();
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

            if (!control && !alt && !windows && key is Key.OemOpenBrackets or Key.Oem6)
            {
                ChangeEventPage(key == Key.Oem6 ? 1 : -1, jumpToEdge: shift);
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

    private static object CreateEventCategoryContent(string assetKey, string fallbackGlyph)
    {
        ImageSource? source = WpfIconRenderer.GetCachedBitmap(IconAssetCache.CategoryPath(assetKey));
        if (source is null)
            return fallbackGlyph;

        return new Image
        {
            Source = source,
            Width = 22,
            Height = 22,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
    }

    private bool TryAddNumberedEvent(int digit)
    {
        if (_level is null || _selection.PrimaryFloor < 0)
            return false;

        EventCategoryDefinition category = GetVisibleEventCategory(EventCategories[_eventCategoryIndex]);
        int slot = digit == 0 ? 9 : digit - 1;
        int index = _eventPageIndex * 10 + slot;
        if ((uint)index >= (uint)category.Events.Length)
            return false;

        return TryAddEventAtSelection(category.Events[index]);
    }

    private void ChangeEventPage(int delta, bool jumpToEdge)
    {
        EventCategoryDefinition category = GetVisibleEventCategory(EventCategories[_eventCategoryIndex]);
        int pageCount = Math.Max(1, (category.Events.Length + 9) / 10);
        if (jumpToEdge)
            _eventPageIndex = delta < 0 ? 0 : pageCount - 1;
        else
            _eventPageIndex = ((_eventPageIndex + delta) % pageCount + pageCount) % pageCount;
        RefreshEventPalette();
    }

    private void OpenEventPicker(object sender, RoutedEventArgs e)
    {
        if (_level is null || _selection.PrimaryFloor < 0)
            return;

        var dialog = new EventPickerWindow(BuildVisibleEventCatalog())
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true && dialog.SelectedEvent is EventCatalogEntry selected)
        {
            int categoryIndex = Array.FindIndex(
                EventCategories,
                category => string.Equals(category.Name, selected.Category, StringComparison.Ordinal));
            if (categoryIndex >= 0)
            {
                _eventCategoryIndex = categoryIndex;
                EventCategoryDefinition visibleCategory = GetVisibleEventCategory(EventCategories[categoryIndex]);
                int eventIndex = Array.IndexOf(visibleCategory.Events, selected.EventType);
                _eventPageIndex = eventIndex < 0 ? 0 : eventIndex / 10;
            }
            RefreshEventPalette();
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

        if (!IsEventAvailableForCurrentEditor(eventType))
        {
            StatusText.Text = $"{eventType} is not available in the current ADOFAI editor context";
            return false;
        }

        EditorSession? editor = EnsureEditorSession();
        int primary = _selection.PrimaryFloor;
        if (editor is null || primary < 0)
            return false;

        int[] selection = _selection.SelectedFloors.ToArray();
        editor.AddAction(primary, eventType);
        RefreshEditorAfterMutation(primary, selection);

        EventList.SelectedItem = EventList.Items
            .Cast<EventListItem>()
            .LastOrDefault(item => string.Equals(item.Action.EventType, eventType, StringComparison.Ordinal));
        StatusText.Text = $"Added {eventType} @ {primary:N0}";
        return true;
    }

    private sealed record EventCategoryDefinition(string Name, string AssetKey, string Glyph, string[] Events);

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

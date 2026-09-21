using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private static readonly Brush EventEditorBackground = new SolidColorBrush(Color.FromRgb(17, 19, 24));
    private static readonly Brush EventEditorForeground = new SolidColorBrush(Color.FromRgb(228, 231, 236));
    private static readonly Brush EventEditorBorder = new SolidColorBrush(Color.FromRgb(48, 52, 59));
    private static readonly Brush EventEditorMuted = new SolidColorBrush(Color.FromRgb(150, 156, 168));

    private static readonly IReadOnlyDictionary<string, EventPropertyDefinition[]> EventPropertySchemas =
        new Dictionary<string, EventPropertyDefinition[]>(StringComparer.Ordinal)
        {
            ["SetSpeed"] =
            [
                EnumProperty("speedType", "Bpm", "Bpm", "Multiplier"),
                NumberProperty("beatsPerMinute", 100.0, "bpm"),
                NumberProperty("bpmMultiplier", 1.0, "X"),
                NumberProperty("angleOffset", 0.0, "°")
            ],
            ["Multitap"] =
            [
                IntegerProperty("taps", 2)
            ],
            ["Checkpoint"] =
            [
                IntegerProperty("tileOffset", 0, "tiles")
            ],
            ["SetHitsound"] =
            [
                TextProperty("gameSound", "Hitsound"),
                TextProperty("hitsound", "Kick"),
                IntegerProperty("hitsoundVolume", 100, "%")
            ],
            ["PlaySound"] =
            [
                TextProperty("hitsound", "Kick"),
                IntegerProperty("hitsoundVolume", 100, "%"),
                NumberProperty("angleOffset", 0.0, "°"),
                TextProperty("eventTag", "")
            ],
            ["Pause"] =
            [
                NumberProperty("duration", 1.0, "beats"),
                IntegerProperty("countdownTicks", 0),
                TextProperty("angleCorrectionDir", "Backward")
            ],
            ["MoveCamera"] =
            [
                NumberProperty("duration", 1.0, "beats"),
                TextProperty("relativeTo", "Player"),
                Vector2Property("position", null, null, "tiles"),
                NumberProperty("rotation", 0.0, "°"),
                NumberProperty("zoom", 100.0, "%"),
                NumberProperty("angleOffset", 0.0, "°"),
                TextProperty("ease", "Linear"),
                BoolProperty("dontDisable", false),
                BoolProperty("minVfxOnly", false),
                TextProperty("eventTag", "")
            ],
            ["Flash"] =
            [
                NumberProperty("duration", 1.0, "beats"),
                TextProperty("plane", "Background"),
                TextProperty("startColor", "ffffff"),
                NumberProperty("startOpacity", 100.0, "%"),
                TextProperty("endColor", "ffffff"),
                NumberProperty("endOpacity", 0.0, "%"),
                NumberProperty("angleOffset", 0.0, "°"),
                TextProperty("ease", "Linear"),
                TextProperty("eventTag", "")
            ]
        };

    private bool _eventPropertyEditorInitialized;
    private StackPanel? _eventPropertyPanel;
    private JsonObject? _eventPropertyDraft;

    private void InitializeEventPropertyEditor()
    {
        if (_eventPropertyEditorInitialized)
            return;
        _eventPropertyEditorInitialized = true;

        EventList.SelectionChanged += (_, _) => RefreshEventPropertyEditor();

        if (EventEditorText.Parent is Grid editorGrid)
        {
            editorGrid.Children.Remove(EventEditorText);

            _eventPropertyPanel = new StackPanel
            {
                Margin = new Thickness(0, 0, 4, 0)
            };

            var rawExpander = new Expander
            {
                Header = "Raw JSON",
                Foreground = EventEditorForeground,
                Margin = new Thickness(0, 8, 0, 0),
                IsExpanded = false,
                Content = EventEditorText
            };
            EventEditorText.MinHeight = 140;
            EventEditorText.Margin = new Thickness(0, 6, 0, 0);
            _eventPropertyPanel.Children.Add(rawExpander);

            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _eventPropertyPanel
            };
            Grid.SetRow(scroller, 2);
            editorGrid.Children.Add(scroller);
        }

        if (FindDescendantButton(this, "Apply Event JSON") is Button applyButton)
        {
            applyButton.Content = "Apply Event";
            applyButton.ToolTip = "Apply property fields / Raw JSON to the selected event";
        }

        RefreshEventPropertyEditor();
    }

    private void RefreshEventPropertyEditor()
    {
        if (_eventPropertyPanel is null)
            return;

        Expander? rawExpander = _eventPropertyPanel.Children.OfType<Expander>().FirstOrDefault();
        _eventPropertyPanel.Children.Clear();

        if (EventList.SelectedItem is not EventListItem item)
        {
            _eventPropertyDraft = null;
            _eventPropertyPanel.Children.Add(new TextBlock
            {
                Text = "Select an event to edit its properties.",
                Foreground = EventEditorMuted,
                TextWrapping = TextWrapping.Wrap
            });
            if (rawExpander is not null)
                _eventPropertyPanel.Children.Add(rawExpander);
            return;
        }

        JsonObject draft;
        try
        {
            draft = JsonNode.Parse(EventEditorText.Text) as JsonObject ?? new JsonObject();
        }
        catch
        {
            draft = new JsonObject();
        }

        string eventType = item.Action.EventType;
        ApplyMissingEventDefaults(eventType, draft);
        _eventPropertyDraft = draft;
        SyncEventDraftToRawJson();

        _eventPropertyPanel.Children.Add(new TextBlock
        {
            Text = eventType,
            Foreground = EventEditorForeground,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        foreach (EventPropertyDefinition property in BuildVisiblePropertyList(eventType, draft))
            _eventPropertyPanel.Children.Add(CreatePropertyRow(property, draft[property.Name]));

        if (rawExpander is not null)
            _eventPropertyPanel.Children.Add(rawExpander);
    }

    private IReadOnlyList<EventPropertyDefinition> BuildVisiblePropertyList(string eventType, JsonObject draft)
    {
        var result = new List<EventPropertyDefinition>
        {
            BoolProperty("active", true)
        };
        var names = new HashSet<string>(StringComparer.Ordinal) { "floor", "eventType", "active" };

        if (EventPropertySchemas.TryGetValue(eventType, out EventPropertyDefinition[]? schema))
        {
            foreach (EventPropertyDefinition property in schema)
            {
                result.Add(property);
                names.Add(property.Name);
            }
        }

        foreach ((string name, JsonNode? value) in draft)
        {
            if (names.Contains(name) || name.EndsWith("RandomMode", StringComparison.Ordinal) ||
                name.EndsWith("RandomValue", StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(InferProperty(name, value));
            names.Add(name);
        }

        return result;
    }

    private FrameworkElement CreatePropertyRow(EventPropertyDefinition property, JsonNode? value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = property.Name,
            Foreground = EventEditorForeground,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = property.Name
        };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        FrameworkElement editor = property.Kind switch
        {
            EventPropertyEditorKind.Bool => CreateBoolEditor(property, value),
            EventPropertyEditorKind.Enum => CreateEnumEditor(property, value),
            EventPropertyEditorKind.Vector2 => CreateVector2Editor(property, value),
            EventPropertyEditorKind.Integer => CreateScalarEditor(property, value, integer: true),
            EventPropertyEditorKind.Number => CreateScalarEditor(property, value, integer: false),
            _ => CreateTextEditor(property, value)
        };
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);

        if (!string.IsNullOrWhiteSpace(property.Unit))
        {
            var unit = new TextBlock
            {
                Text = property.Unit,
                Foreground = EventEditorMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            Grid.SetColumn(unit, 2);
            row.Children.Add(unit);
        }

        return row;
    }

    private FrameworkElement CreateBoolEditor(EventPropertyDefinition property, JsonNode? value)
    {
        bool current = TryReadBool(value, out bool parsed) ? parsed : Convert.ToBoolean(property.Default ?? false, CultureInfo.InvariantCulture);
        var check = new CheckBox
        {
            IsChecked = current,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = EventEditorForeground
        };
        check.Checked += (_, _) => SetEventDraftValue(property.Name, JsonValue.Create(true));
        check.Unchecked += (_, _) => SetEventDraftValue(property.Name, JsonValue.Create(false));
        return check;
    }

    private FrameworkElement CreateEnumEditor(EventPropertyDefinition property, JsonNode? value)
    {
        string current = ReadString(value) ?? Convert.ToString(property.Default, CultureInfo.InvariantCulture) ?? string.Empty;
        if (property.Options is not { Length: > 0 })
            return CreateTextEditor(property, value);

        var combo = new ComboBox
        {
            ItemsSource = property.Options,
            SelectedItem = current,
            MinHeight = 24
        };
        if (combo.SelectedIndex < 0 && property.Options.Length > 0)
            combo.SelectedIndex = 0;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string selected)
                SetEventDraftValue(property.Name, JsonValue.Create(selected));
        };
        return combo;
    }

    private FrameworkElement CreateTextEditor(EventPropertyDefinition property, JsonNode? value)
    {
        string text = ReadString(value) ?? Convert.ToString(property.Default, CultureInfo.InvariantCulture) ?? string.Empty;
        var box = CreateEditorTextBox(text);
        box.LostKeyboardFocus += (_, _) => SetEventDraftValue(property.Name, JsonValue.Create(box.Text));
        return box;
    }

    private FrameworkElement CreateScalarEditor(EventPropertyDefinition property, JsonNode? value, bool integer)
    {
        string text = ReadScalarText(value);
        if (string.IsNullOrWhiteSpace(text) && property.Default is not null)
            text = Convert.ToString(property.Default, CultureInfo.InvariantCulture) ?? string.Empty;

        var box = CreateEditorTextBox(text);
        box.LostKeyboardFocus += (_, _) =>
        {
            string candidate = box.Text.Trim();
            if (integer)
            {
                if (int.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue))
                    SetEventDraftValue(property.Name, JsonValue.Create(intValue));
            }
            else if (double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleValue))
            {
                SetEventDraftValue(property.Name, JsonValue.Create(doubleValue));
            }
        };
        return box;
    }

    private FrameworkElement CreateVector2Editor(EventPropertyDefinition property, JsonNode? value)
    {
        var pair = value as JsonArray;
        string x = pair is { Count: > 0 } ? ReadScalarText(pair[0]) : ReadDefaultVectorComponent(property.Default, 0);
        string y = pair is { Count: > 1 } ? ReadScalarText(pair[1]) : ReadDefaultVectorComponent(property.Default, 1);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        TextBox xBox = CreateEditorTextBox(x);
        TextBox yBox = CreateEditorTextBox(y);
        Grid.SetColumn(xBox, 0);
        Grid.SetColumn(yBox, 2);
        grid.Children.Add(xBox);
        grid.Children.Add(yBox);

        void Commit()
        {
            JsonNode? xNode = ParseVectorComponentNode(xBox.Text);
            JsonNode? yNode = ParseVectorComponentNode(yBox.Text);
            SetEventDraftValue(property.Name, new JsonArray(xNode, yNode));
        }

        xBox.LostKeyboardFocus += (_, _) => Commit();
        yBox.LostKeyboardFocus += (_, _) => Commit();
        return grid;
    }

    private static TextBox CreateEditorTextBox(string text) => new()
    {
        Text = text,
        MinHeight = 24,
        Padding = new Thickness(4, 2, 4, 2),
        Background = EventEditorBackground,
        Foreground = EventEditorForeground,
        BorderBrush = EventEditorBorder,
        BorderThickness = new Thickness(1)
    };

    private void SetEventDraftValue(string propertyName, JsonNode? value)
    {
        if (_eventPropertyDraft is null)
            return;
        _eventPropertyDraft[propertyName] = value;
        SyncEventDraftToRawJson();
    }

    private void SyncEventDraftToRawJson()
    {
        if (_eventPropertyDraft is null)
            return;
        EventEditorText.Text = _eventPropertyDraft.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void ApplyMissingEventDefaults(string eventType, JsonObject draft)
    {
        if (!EventPropertySchemas.TryGetValue(eventType, out EventPropertyDefinition[]? schema))
            return;

        foreach (EventPropertyDefinition property in schema)
        {
            if (draft.ContainsKey(property.Name))
                continue;
            draft[property.Name] = ConvertDefaultToNode(property.Default);
        }
    }

    private static EventPropertyDefinition InferProperty(string name, JsonNode? value)
    {
        if (value is JsonArray { Count: 2 })
            return Vector2Property(name, null, null);
        if (TryReadBool(value, out _))
            return BoolProperty(name, false);
        if (TryReadInteger(value, out _))
            return IntegerProperty(name, 0);
        if (TryReadDouble(value, out _))
            return NumberProperty(name, 0.0);
        return TextProperty(name, ReadString(value) ?? value?.ToJsonString() ?? string.Empty);
    }

    private static JsonNode? ConvertDefaultToNode(object? value)
    {
        return value switch
        {
            null => null,
            string text => JsonValue.Create(text),
            bool boolean => JsonValue.Create(boolean),
            int integer => JsonValue.Create(integer),
            double number => JsonValue.Create(number),
            double?[] pair => new JsonArray(pair.Select(component => component is null ? null : JsonValue.Create(component.Value)).ToArray()),
            _ => JsonSerializer.SerializeToNode(value)
        };
    }

    private static JsonNode? ParseVectorComponentNode(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
            return null;
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? JsonValue.Create(value)
            : JsonValue.Create(trimmed);
    }

    private static bool TryReadBool(JsonNode? node, out bool value)
    {
        if (node is JsonValue json && json.TryGetValue(out value))
            return true;
        value = false;
        return false;
    }

    private static bool TryReadInteger(JsonNode? node, out int value)
    {
        if (node is JsonValue json && json.TryGetValue(out value))
            return true;
        value = 0;
        return false;
    }

    private static bool TryReadDouble(JsonNode? node, out double value)
    {
        if (node is JsonValue json)
        {
            if (json.TryGetValue(out value))
                return true;
            if (json.TryGetValue(out int integer))
            {
                value = integer;
                return true;
            }
        }
        value = 0;
        return false;
    }

    private static string? ReadString(JsonNode? node)
    {
        if (node is JsonValue json && json.TryGetValue(out string? value))
            return value;
        return null;
    }

    private static string ReadScalarText(JsonNode? node)
    {
        if (node is null)
            return string.Empty;
        if (node is JsonValue json)
        {
            if (json.TryGetValue(out double number))
                return number.ToString("G", CultureInfo.InvariantCulture);
            if (json.TryGetValue(out int integer))
                return integer.ToString(CultureInfo.InvariantCulture);
            if (json.TryGetValue(out string? text))
                return text ?? string.Empty;
        }
        return node.ToJsonString();
    }

    private static string ReadDefaultVectorComponent(object? value, int index)
    {
        if (value is double?[] numericPair && index < numericPair.Length && numericPair[index] is double numeric)
            return numeric.ToString("G", CultureInfo.InvariantCulture);
        if (value is object?[] pair && index < pair.Length)
            return Convert.ToString(pair[index], CultureInfo.InvariantCulture) ?? string.Empty;
        return string.Empty;
    }

    private static EventPropertyDefinition BoolProperty(string name, bool defaultValue) =>
        new(name, EventPropertyEditorKind.Bool, defaultValue, string.Empty, null);

    private static EventPropertyDefinition IntegerProperty(string name, int defaultValue, string unit = "") =>
        new(name, EventPropertyEditorKind.Integer, defaultValue, unit, null);

    private static EventPropertyDefinition NumberProperty(string name, double defaultValue, string unit = "") =>
        new(name, EventPropertyEditorKind.Number, defaultValue, unit, null);

    private static EventPropertyDefinition TextProperty(string name, string defaultValue) =>
        new(name, EventPropertyEditorKind.Text, defaultValue, string.Empty, null);

    private static EventPropertyDefinition EnumProperty(string name, string defaultValue, params string[] options) =>
        new(name, EventPropertyEditorKind.Enum, defaultValue, string.Empty, options);

    private static EventPropertyDefinition Vector2Property(string name, double? x, double? y, string unit = "") =>
        new(name, EventPropertyEditorKind.Vector2, new double?[] { x, y }, unit, null);

    private enum EventPropertyEditorKind
    {
        Text,
        Bool,
        Integer,
        Number,
        Enum,
        Vector2
    }

    private sealed record EventPropertyDefinition(
        string Name,
        EventPropertyEditorKind Kind,
        object? Default,
        string Unit,
        string[]? Options);
}

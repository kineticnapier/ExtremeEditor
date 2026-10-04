using System.Globalization;
using System.Text.Json.Nodes;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf;

internal sealed partial class EditorSession
{
    public TrackSettingsSnapshot GetTrackSettings()
    {
        TrackVisualStyle style = TrackVisualMetadataCache.Get(Document).InitialStyle;
        _ = TrackAnimationMetadataCache.TryGet(Document, out TrackAnimationSettingsData animation);
        return new TrackSettingsSnapshot(
            style.ColorType,
            style.PrimaryColor,
            style.SecondaryColor,
            style.AnimDuration,
            style.PulseType,
            style.PulseLength,
            style.TrackStyle,
            style.GlowIntensity,
            animation.TrackAnimation,
            animation.BeatsAhead,
            animation.TrackDisappearAnimation,
            animation.BeatsBehind);
    }

    public void EditTrackSettings(TrackSettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        TrackSettingsSnapshot before = GetTrackSettings();
        TrackSettingsSnapshot requested = settings.HasTrackAnimationSettings
            ? settings
            : settings with
            {
                TrackAnimation = before.TrackAnimation,
                BeatsAhead = before.BeatsAhead,
                TrackDisappearAnimation = before.TrackDisappearAnimation,
                BeatsBehind = before.BeatsBehind,
                HasTrackAnimationSettings = true
            };
        TrackSettingsSnapshot normalized = NormalizeTrackSettings(requested);
        if (before == normalized)
            return;
        Execute(new EditTrackSettingsCommand(before, normalized));
    }

    internal void ApplyTrackSettingsRaw(TrackSettingsSnapshot settings)
    {
        TrackVisualSourceData current = TrackVisualMetadataCache.Get(Document);
        TrackVisualStyle style = current.InitialStyle with
        {
            ColorType = settings.TrackColorType,
            PrimaryColor = settings.TrackColor,
            SecondaryColor = settings.SecondaryTrackColor,
            AnimDuration = settings.TrackColorAnimDuration,
            PulseType = settings.TrackColorPulse,
            PulseLength = settings.TrackPulseLength,
            TrackStyle = settings.TrackStyle,
            GlowIntensity = settings.TrackGlowIntensity
        };
        TrackVisualMetadataCache.Attach(Document, current with { InitialStyle = style });

        TrackColorSourceData legacy = TrackColorMetadataCache.Get(Document);
        TrackColorMetadataCache.Attach(Document, legacy with
        {
            InitialStyle = new TrackColorStyle(
                settings.TrackColorType,
                settings.TrackColor,
                settings.SecondaryTrackColor,
                settings.TrackPulseLength)
        });

        _ = TrackAnimationMetadataCache.TryGet(Document, out TrackAnimationSettingsData currentAnimation);
        TrackAnimationMetadataCache.Attach(
            Document,
            currentAnimation with
            {
                TrackAnimation = settings.TrackAnimation,
                BeatsAhead = settings.BeatsAhead,
                TrackDisappearAnimation = settings.TrackDisappearAnimation,
                BeatsBehind = settings.BeatsBehind
            });

        JsonObject root = EnsureSourceRoot();
        JsonObject sourceSettings = root["settings"] as JsonObject ?? new JsonObject();
        sourceSettings["trackColor"] = settings.TrackColor;
        sourceSettings["secondaryTrackColor"] = settings.SecondaryTrackColor;
        sourceSettings["trackColorType"] = settings.TrackColorType;
        sourceSettings["trackColorAnimDuration"] = settings.TrackColorAnimDuration;
        sourceSettings["trackColorPulse"] = settings.TrackColorPulse;
        sourceSettings["trackPulseLength"] = settings.TrackPulseLength;
        sourceSettings["trackStyle"] = settings.TrackStyle;
        sourceSettings["trackGlowIntensity"] = settings.TrackGlowIntensity;
        sourceSettings["trackAnimation"] = settings.TrackAnimation;
        sourceSettings["beatsAhead"] = settings.BeatsAhead;
        sourceSettings["trackDisappearAnimation"] = settings.TrackDisappearAnimation;
        sourceSettings["beatsBehind"] = settings.BeatsBehind;
        root["settings"] = sourceSettings;
    }

    internal static bool IsValidTrackColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        string hex = value.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];
        return (hex.Length is 6 or 8) &&
               hex.All(static character => Uri.IsHexDigit(character));
    }

    private static TrackSettingsSnapshot NormalizeTrackSettings(TrackSettingsSnapshot settings)
    {
        static string Required(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"{name} must not be empty.", name);
            return value.Trim();
        }

        string primary = Required(settings.TrackColor, nameof(settings.TrackColor));
        string secondary = Required(settings.SecondaryTrackColor, nameof(settings.SecondaryTrackColor));
        if (!IsValidTrackColor(primary))
            throw new ArgumentException("Track color must contain 6 or 8 hexadecimal digits.", nameof(settings.TrackColor));
        if (!IsValidTrackColor(secondary))
            throw new ArgumentException(
                "Secondary track color must contain 6 or 8 hexadecimal digits.",
                nameof(settings.SecondaryTrackColor));
        if (!double.IsFinite(settings.TrackColorAnimDuration) || settings.TrackColorAnimDuration <= 0.0)
            throw new ArgumentOutOfRangeException(
                nameof(settings.TrackColorAnimDuration),
                "Track color animation duration must be finite and positive.");
        if (settings.TrackPulseLength < 1)
            throw new ArgumentOutOfRangeException(
                nameof(settings.TrackPulseLength),
                "Track pulse length must be at least one.");
        if (!double.IsFinite(settings.TrackGlowIntensity) ||
            settings.TrackGlowIntensity is < 0.0 or > 100.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.TrackGlowIntensity),
                "Track glow intensity must be between 0 and 100.");
        }
        if (!double.IsFinite(settings.BeatsAhead) || settings.BeatsAhead < 0.0)
            throw new ArgumentOutOfRangeException(nameof(settings.BeatsAhead));
        if (!double.IsFinite(settings.BeatsBehind) || settings.BeatsBehind < 0.0)
            throw new ArgumentOutOfRangeException(nameof(settings.BeatsBehind));

        return settings with
        {
            TrackColorType = Required(settings.TrackColorType, nameof(settings.TrackColorType)),
            TrackColor = primary,
            SecondaryTrackColor = secondary,
            TrackColorPulse = Required(settings.TrackColorPulse, nameof(settings.TrackColorPulse)),
            TrackStyle = Required(settings.TrackStyle, nameof(settings.TrackStyle)),
            TrackAnimation = Required(settings.TrackAnimation, nameof(settings.TrackAnimation)),
            TrackDisappearAnimation = Required(
                settings.TrackDisappearAnimation,
                nameof(settings.TrackDisappearAnimation))
        };
    }

    private sealed class EditTrackSettingsCommand(
        TrackSettingsSnapshot before,
        TrackSettingsSnapshot after) : IEditorCommand
    {
        public string Name => "Edit track settings";
        public void Execute(EditorSession session) => session.ApplyTrackSettingsRaw(after);
        public void Undo(EditorSession session) => session.ApplyTrackSettingsRaw(before);
    }
}

using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal static class TrackVisualRuntimeEvaluator
{
    internal static NativeTrackVisual Evaluate(
        NativeTrackVisual baseVisual,
        IReadOnlyList<NativeTrackVisualEvent> events,
        int floor,
        double chartTime)
    {
        NativeTrackVisual state = baseVisual;
        uint startPrimary = state.PrimaryColor;
        uint startSecondary = state.SecondaryColor;
        uint targetPrimary = state.PrimaryColor;
        uint targetSecondary = state.SecondaryColor;
        NativeTrackVisualEvent? activeTween = null;

        foreach (NativeTrackVisualEvent item in events
                     .Where(item => floor >= Math.Min(item.StartFloor, item.EndFloor) &&
                                    floor <= Math.Max(item.StartFloor, item.EndFloor) &&
                                    (floor - Math.Min(item.StartFloor, item.EndFloor)) % (item.GapLength + 1) == 0)
                     .OrderBy(static item => item.StartTime)
                     .ThenBy(static item => item.SourceIndex))
        {
            if (item.StartTime > chartTime)
                break;
            if (activeTween is not null)
                state = state with { PrimaryColor = targetPrimary, SecondaryColor = targetSecondary };

            startPrimary = state.PrimaryColor;
            startSecondary = state.SecondaryColor;
            if ((item.VisualFlags & 7u) == 1u)
            {
                bool secondary = ((floor - item.StartFloor) & 1) != 0;
                targetPrimary = secondary ? item.SecondaryColor : item.PrimaryColor;
                targetSecondary = targetPrimary;
            }
            else
            {
                targetPrimary = item.PrimaryColor;
                targetSecondary = item.SecondaryColor;
            }

            state = state with
            {
                Flags = item.VisualFlags,
                AnimDuration = item.AnimDuration,
                GlowIntensity = item.GlowIntensity,
                StartFloor = item.StartFloor,
                PulseLength = item.PulseLength
            };
            if (item.TransitionDuration <= 0)
            {
                state = state with { PrimaryColor = targetPrimary, SecondaryColor = targetSecondary };
                activeTween = null;
            }
            else
            {
                activeTween = item;
            }
        }

        if (activeTween is NativeTrackVisualEvent tween)
        {
            float progress = EvaluateEase(tween.Ease, chartTime - tween.StartTime, tween.TransitionDuration);
            state = state with
            {
                PrimaryColor = LerpColor(startPrimary, targetPrimary, progress),
                SecondaryColor = LerpColor(startSecondary, targetSecondary, progress)
            };
        }
        return state;
    }

    private static float EvaluateEase(uint ease, double elapsed, double duration)
    {
        string name = ease switch
        {
            NativeCameraEvent.EaseInSine => "InSine",
            NativeCameraEvent.EaseOutSine => "OutSine",
            NativeCameraEvent.EaseInOutSine => "InOutSine",
            NativeCameraEvent.EaseInQuad => "InQuad",
            NativeCameraEvent.EaseOutQuad => "OutQuad",
            NativeCameraEvent.EaseInOutQuad => "InOutQuad",
            _ => "Linear"
        };
        return DotweenEaseEvaluator.TryEvaluate(name, elapsed, duration, out float value)
            ? value
            : (float)Math.Clamp(elapsed / duration, 0, 1);
    }

    private static uint LerpColor(uint from, uint to, float progress)
    {
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            float a = (from >> shift) & 0xffu;
            float b = (to >> shift) & 0xffu;
            uint value = checked((uint)Math.Clamp((int)MathF.Round(a + ((b - a) * progress)), 0, 255));
            result |= value << shift;
        }
        return result;
    }
}

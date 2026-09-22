using System.Numerics;

namespace ExtremeEditor.Wpf.Native;

internal static class TrackTransformPositionSampler
{
    internal static Vector2 Evaluate(
        int floor,
        double chartTime,
        StaticTrackTransform[] staticTransforms,
        NativeTrackTransformEvent[] moveTimeline)
    {
        if ((uint)floor >= (uint)staticTransforms.Length)
            return Vector2.Zero;

        float x = staticTransforms[floor].X;
        float y = staticTransforms[floor].Y;
        NativeTrackTransformEvent? xEvent = null;
        NativeTrackTransformEvent? yEvent = null;

        foreach (NativeTrackTransformEvent item in moveTimeline)
        {
            if (item.Floor != floor || item.StartTime > chartTime)
                continue;

            if ((item.Flags & NativeTrackTransformEvent.FlagX) != 0u &&
                (xEvent is null || item.StartTime >= xEvent.Value.StartTime))
            {
                xEvent = item;
            }

            if ((item.Flags & NativeTrackTransformEvent.FlagY) != 0u &&
                (yEvent is null || item.StartTime >= yEvent.Value.StartTime))
            {
                yEvent = item;
            }
        }

        if (xEvent is NativeTrackTransformEvent xe)
            x = EvaluateAxis(xe.StartX, xe.TargetX, xe.StartTime, xe.DurationSeconds, xe.Ease, chartTime);
        if (yEvent is NativeTrackTransformEvent ye)
            y = EvaluateAxis(ye.StartY, ye.TargetY, ye.StartTime, ye.DurationSeconds, ye.Ease, chartTime);

        return new Vector2(x, y);
    }

    private static float EvaluateAxis(
        float start,
        float target,
        double startTime,
        double duration,
        uint ease,
        double chartTime)
    {
        double linear = duration <= 1e-9
            ? 1.0
            : Math.Clamp((chartTime - startTime) / duration, 0.0, 1.0);
        float progress = (float)ApplyEase(ease, linear);
        return start + (target - start) * progress;
    }

    private static double ApplyEase(uint ease, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        const double c1 = 1.70158;
        const double c2 = c1 * 1.525;
        const double c3 = c1 + 1.0;
        double c4 = 2.0 * Math.PI / 3.0;
        double c5 = 2.0 * Math.PI / 4.5;

        return ease switch
        {
            NativeCameraEvent.EaseInSine => 1.0 - Math.Cos(t * Math.PI / 2.0),
            NativeCameraEvent.EaseOutSine => Math.Sin(t * Math.PI / 2.0),
            NativeCameraEvent.EaseInOutSine => -(Math.Cos(Math.PI * t) - 1.0) / 2.0,
            NativeCameraEvent.EaseInQuad => t * t,
            NativeCameraEvent.EaseOutQuad => 1.0 - (1.0 - t) * (1.0 - t),
            NativeCameraEvent.EaseInOutQuad => t < 0.5 ? 2.0 * t * t : 1.0 - Math.Pow(-2.0 * t + 2.0, 2.0) / 2.0,
            NativeCameraEvent.EaseInCubic => t * t * t,
            NativeCameraEvent.EaseOutCubic => 1.0 - Math.Pow(1.0 - t, 3.0),
            NativeCameraEvent.EaseInOutCubic => t < 0.5 ? 4.0 * t * t * t : 1.0 - Math.Pow(-2.0 * t + 2.0, 3.0) / 2.0,
            NativeCameraEvent.EaseInQuart => Math.Pow(t, 4.0),
            NativeCameraEvent.EaseOutQuart => 1.0 - Math.Pow(1.0 - t, 4.0),
            NativeCameraEvent.EaseInOutQuart => t < 0.5 ? 8.0 * Math.Pow(t, 4.0) : 1.0 - Math.Pow(-2.0 * t + 2.0, 4.0) / 2.0,
            NativeCameraEvent.EaseInQuint => Math.Pow(t, 5.0),
            NativeCameraEvent.EaseOutQuint => 1.0 - Math.Pow(1.0 - t, 5.0),
            NativeCameraEvent.EaseInOutQuint => t < 0.5 ? 16.0 * Math.Pow(t, 5.0) : 1.0 - Math.Pow(-2.0 * t + 2.0, 5.0) / 2.0,
            NativeCameraEvent.EaseInExpo => t <= 0.0 ? 0.0 : Math.Pow(2.0, 10.0 * t - 10.0),
            NativeCameraEvent.EaseOutExpo => t >= 1.0 ? 1.0 : 1.0 - Math.Pow(2.0, -10.0 * t),
            NativeCameraEvent.EaseInOutExpo => t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0 : t < 0.5
                ? Math.Pow(2.0, 20.0 * t - 10.0) / 2.0
                : (2.0 - Math.Pow(2.0, -20.0 * t + 10.0)) / 2.0,
            NativeCameraEvent.EaseInCirc => 1.0 - Math.Sqrt(Math.Max(0.0, 1.0 - t * t)),
            NativeCameraEvent.EaseOutCirc => Math.Sqrt(Math.Max(0.0, 1.0 - Math.Pow(t - 1.0, 2.0))),
            NativeCameraEvent.EaseInOutCirc => t < 0.5
                ? (1.0 - Math.Sqrt(Math.Max(0.0, 1.0 - Math.Pow(2.0 * t, 2.0)))) / 2.0
                : (Math.Sqrt(Math.Max(0.0, 1.0 - Math.Pow(-2.0 * t + 2.0, 2.0))) + 1.0) / 2.0,
            NativeCameraEvent.EaseInBack => c3 * t * t * t - c1 * t * t,
            NativeCameraEvent.EaseOutBack => 1.0 + c3 * Math.Pow(t - 1.0, 3.0) + c1 * Math.Pow(t - 1.0, 2.0),
            NativeCameraEvent.EaseInOutBack => t < 0.5
                ? Math.Pow(2.0 * t, 2.0) * ((c2 + 1.0) * 2.0 * t - c2) / 2.0
                : (Math.Pow(2.0 * t - 2.0, 2.0) * ((c2 + 1.0) * (2.0 * t - 2.0) + c2) + 2.0) / 2.0,
            NativeCameraEvent.EaseInElastic => t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0
                : -Math.Pow(2.0, 10.0 * t - 10.0) * Math.Sin((t * 10.0 - 10.75) * c4),
            NativeCameraEvent.EaseOutElastic => t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0
                : Math.Pow(2.0, -10.0 * t) * Math.Sin((t * 10.0 - 0.75) * c4) + 1.0,
            NativeCameraEvent.EaseInOutElastic => t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0 : t < 0.5
                ? -(Math.Pow(2.0, 20.0 * t - 10.0) * Math.Sin((20.0 * t - 11.125) * c5)) / 2.0
                : Math.Pow(2.0, -20.0 * t + 10.0) * Math.Sin((20.0 * t - 11.125) * c5) / 2.0 + 1.0,
            NativeCameraEvent.EaseInBounce => 1.0 - OutBounce(1.0 - t),
            NativeCameraEvent.EaseOutBounce => OutBounce(t),
            NativeCameraEvent.EaseInOutBounce => t < 0.5
                ? (1.0 - OutBounce(1.0 - 2.0 * t)) / 2.0
                : (1.0 + OutBounce(2.0 * t - 1.0)) / 2.0,
            _ => t
        };
    }

    private static double OutBounce(double t)
    {
        const double n1 = 7.5625;
        const double d1 = 2.75;
        if (t < 1.0 / d1)
            return n1 * t * t;
        if (t < 2.0 / d1)
        {
            t -= 1.5 / d1;
            return n1 * t * t + 0.75;
        }
        if (t < 2.5 / d1)
        {
            t -= 2.25 / d1;
            return n1 * t * t + 0.9375;
        }

        t -= 2.625 / d1;
        return n1 * t * t + 0.984375;
    }
}

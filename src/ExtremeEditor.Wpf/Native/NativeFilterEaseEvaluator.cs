namespace ExtremeEditor.Wpf.Native;

// Mirrors the easing function used by the native filter timeline in renderer.cpp.
// Keep the numeric formulas in step with ApplyFlashEase when it changes.
internal static class NativeFilterEaseEvaluator
{
    private const double Pi = Math.PI;
    private const double C1 = 1.70158;
    private const double C2 = C1 * 1.525;
    private const double C3 = C1 + 1.0;
    private const double C4 = 2.0 * Pi / 3.0;
    private const double C5 = 2.0 * Pi / 4.5;

    internal static double Evaluate(uint ease, double raw)
    {
        double t = Math.Clamp(raw, 0.0, 1.0);
        double value = ease switch
        {
            NativeCameraEvent.EaseInSine => 1.0 - Math.Cos(t * Pi / 2.0),
            NativeCameraEvent.EaseOutSine => Math.Sin(t * Pi / 2.0),
            NativeCameraEvent.EaseInOutSine => -(Math.Cos(Pi * t) - 1.0) / 2.0,
            NativeCameraEvent.EaseInQuad => t * t,
            NativeCameraEvent.EaseOutQuad => 1.0 - (1.0 - t) * (1.0 - t),
            NativeCameraEvent.EaseInOutQuad => t < 0.5 ? 2.0 * t * t : 1.0 - Math.Pow(-2.0 * t + 2.0, 2.0) / 2.0,
            NativeCameraEvent.EaseInCubic => t * t * t,
            NativeCameraEvent.EaseOutCubic => 1.0 - Math.Pow(1.0 - t, 3.0),
            NativeCameraEvent.EaseInOutCubic => t < 0.5 ? 4.0 * t * t * t : 1.0 - Math.Pow(-2.0 * t + 2.0, 3.0) / 2.0,
            NativeCameraEvent.EaseInQuart => t * t * t * t,
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
            NativeCameraEvent.EaseInBack => C3 * t * t * t - C1 * t * t,
            NativeCameraEvent.EaseOutBack => 1.0 + C3 * Math.Pow(t - 1.0, 3.0) + C1 * Math.Pow(t - 1.0, 2.0),
            NativeCameraEvent.EaseInOutBack => t < 0.5
                ? Math.Pow(2.0 * t, 2.0) * ((C2 + 1.0) * 2.0 * t - C2) / 2.0
                : (Math.Pow(2.0 * t - 2.0, 2.0) * ((C2 + 1.0) * (2.0 * t - 2.0) + C2) + 2.0) / 2.0,
            NativeCameraEvent.EaseInElastic => t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0
                : -Math.Pow(2.0, 10.0 * t - 10.0) * Math.Sin((t * 10.0 - 10.75) * C4),
            NativeCameraEvent.EaseOutElastic => t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0
                : Math.Pow(2.0, -10.0 * t) * Math.Sin((t * 10.0 - 0.75) * C4) + 1.0,
            NativeCameraEvent.EaseInOutElastic => t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0 : t < 0.5
                ? -(Math.Pow(2.0, 20.0 * t - 10.0) * Math.Sin((20.0 * t - 11.125) * C5)) / 2.0
                : Math.Pow(2.0, -20.0 * t + 10.0) * Math.Sin((20.0 * t - 11.125) * C5) / 2.0 + 1.0,
            NativeCameraEvent.EaseInBounce => 1.0 - OutBounce(1.0 - t),
            NativeCameraEvent.EaseOutBounce => OutBounce(t),
            NativeCameraEvent.EaseInOutBounce => t < 0.5
                ? (1.0 - OutBounce(1.0 - 2.0 * t)) / 2.0
                : (1.0 + OutBounce(2.0 * t - 1.0)) / 2.0,
            _ => t
        };
        return double.IsFinite(value) ? value : t;
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

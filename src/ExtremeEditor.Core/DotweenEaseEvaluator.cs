using System.Globalization;

namespace ExtremeEditor.Core;

/// <summary>DG.Tweening.Ease, value for value.</summary>
public enum DotweenEase
{
    Unset = 0,
    Linear = 1,
    InSine = 2, OutSine = 3, InOutSine = 4,
    InQuad = 5, OutQuad = 6, InOutQuad = 7,
    InCubic = 8, OutCubic = 9, InOutCubic = 10,
    InQuart = 11, OutQuart = 12, InOutQuart = 13,
    InQuint = 14, OutQuint = 15, InOutQuint = 16,
    InExpo = 17, OutExpo = 18, InOutExpo = 19,
    InCirc = 20, OutCirc = 21, InOutCirc = 22,
    InElastic = 23, OutElastic = 24, InOutElastic = 25,
    InBack = 26, OutBack = 27, InOutBack = 28,
    InBounce = 29, OutBounce = 30, InOutBounce = 31,
    Flash = 32, InFlash = 33, OutFlash = 34, InOutFlash = 35,
    INTERNAL_Zero = 36,
    INTERNAL_Custom = 37
}

/// <summary>
/// Port of the DOTween runtime bundled with ADOFAI
/// (DG.Tweening.Core.Easing.EaseManager.Evaluate, Bounce and Flash), in the
/// same float precision and operation order. ffxMoveDecorationsPlus tweens
/// with DOTween.To(...).SetEase(ease), so the defaults apply:
/// overshoot/amplitude 1.70158 and period 0.
///
/// Ease names are parsed like the game's Enum.Parse: case-sensitive names,
/// surrounding whitespace ignored, numeric values accepted. A missing or empty
/// ease is the event default, Linear. A name the game cannot parse (and
/// INTERNAL_Custom, which has no custom curve in a chart) falls back to
/// Linear so the event is still applied and reaches its target.
/// </summary>
public static class DotweenEaseEvaluator
{
    public const float DefaultOvershootOrAmplitude = 1.70158f;
    public const float DefaultPeriod = 0f;

    private const float PiOver2 = 1.5707964f;
    private const float Pi = 3.1415927f;
    private const float TwoPi = 6.2831855f;

    /// <summary>True when the game parses <paramref name="ease"/> to a DOTween ease.</summary>
    public static bool IsSupported(string? ease) => TryParse(ease, out _);

    public static bool TryParse(string? ease, out DotweenEase value)
    {
        if (string.IsNullOrEmpty(ease))
        {
            value = DotweenEase.Linear;
            return true;
        }

        string text = ease.Trim();
        if (text.Length > 0 &&
            (char.IsDigit(text[0]) || text[0] is '-' or '+') &&
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
        {
            // Enum.Parse accepts any integer; undefined values reach the
            // DOTween default branch.
            value = (DotweenEase)number;
            return true;
        }

        if (text.Length > 0 && char.IsLetter(text[0]) &&
            Enum.TryParse(text, ignoreCase: false, out value) &&
            Enum.IsDefined(value) &&
            value != DotweenEase.INTERNAL_Custom)
        {
            return true;
        }

        value = DotweenEase.Linear;
        return false;
    }

    /// <summary>The ease a MoveDecorations event uses; unknown names use Linear.</summary>
    public static DotweenEase Resolve(string? ease) =>
        TryParse(ease, out DotweenEase value) ? value : DotweenEase.Linear;

    /// <summary>
    /// Eased progress of a tween <paramref name="elapsedSeconds"/> after it
    /// started. Elapsed time is limited to [0, duration]; the eased value is
    /// not, so Back and Elastic overshoot. At and after the end the value is
    /// DOTween's completed value (1 for every ease but the Flash family).
    /// A non-positive duration completes at once. Returns false only for a
    /// non-finite time or duration.
    /// </summary>
    public static bool TryEvaluate(
        string? ease,
        double elapsedSeconds,
        double durationSeconds,
        out float easedProgress)
    {
        if (!double.IsFinite(elapsedSeconds) || !double.IsFinite(durationSeconds))
        {
            easedProgress = 0;
            return false;
        }

        DotweenEase resolved = Resolve(ease);
        float duration = (float)durationSeconds;
        if (!(duration > 0f))
        {
            easedProgress = 1;
            return true;
        }

        if (elapsedSeconds <= 0)
        {
            easedProgress = 0;
            return true;
        }

        // DOTween receives float time and duration.
        float time = elapsedSeconds >= durationSeconds ? duration : Math.Min((float)elapsedSeconds, duration);
        float value = Evaluate(resolved, time, duration, DefaultOvershootOrAmplitude, DefaultPeriod);
        easedProgress = float.IsFinite(value) ? value : time / duration;
        return true;
    }

    /// <summary>DG.Tweening.Core.Easing.EaseManager.Evaluate.</summary>
    public static float Evaluate(
        DotweenEase ease,
        float time,
        float duration,
        float overshootOrAmplitude,
        float period)
    {
        switch (ease)
        {
            case DotweenEase.Linear:
            case DotweenEase.INTERNAL_Custom:
                return time / duration;
            case DotweenEase.InSine:
                return -(float)Math.Cos(time / duration * PiOver2) + 1f;
            case DotweenEase.OutSine:
                return (float)Math.Sin(time / duration * PiOver2);
            case DotweenEase.InOutSine:
                return -0.5f * ((float)Math.Cos(Pi * time / duration) - 1f);
            case DotweenEase.InQuad:
                return (time /= duration) * time;
            case DotweenEase.OutQuad:
                return -(time /= duration) * (time - 2f);
            case DotweenEase.InOutQuad:
                if ((time /= duration * 0.5f) < 1f)
                    return 0.5f * time * time;
                return -0.5f * ((--time) * (time - 2f) - 1f);
            case DotweenEase.InCubic:
                return (time /= duration) * time * time;
            case DotweenEase.OutCubic:
                return (time = time / duration - 1f) * time * time + 1f;
            case DotweenEase.InOutCubic:
                if ((time /= duration * 0.5f) < 1f)
                    return 0.5f * time * time * time;
                return 0.5f * ((time -= 2f) * time * time + 2f);
            case DotweenEase.InQuart:
                return (time /= duration) * time * time * time;
            case DotweenEase.OutQuart:
                return -((time = time / duration - 1f) * time * time * time - 1f);
            case DotweenEase.InOutQuart:
                if ((time /= duration * 0.5f) < 1f)
                    return 0.5f * time * time * time * time;
                return -0.5f * ((time -= 2f) * time * time * time - 2f);
            case DotweenEase.InQuint:
                return (time /= duration) * time * time * time * time;
            case DotweenEase.OutQuint:
                return (time = time / duration - 1f) * time * time * time * time + 1f;
            case DotweenEase.InOutQuint:
                if ((time /= duration * 0.5f) < 1f)
                    return 0.5f * time * time * time * time * time;
                return 0.5f * ((time -= 2f) * time * time * time * time + 2f);
            case DotweenEase.InExpo:
                return time == 0f ? 0f : (float)Math.Pow(2.0, 10f * (time / duration - 1f));
            case DotweenEase.OutExpo:
                return time == duration ? 1f : -(float)Math.Pow(2.0, -10f * time / duration) + 1f;
            case DotweenEase.InOutExpo:
                if (time == 0f)
                    return 0f;
                if (time == duration)
                    return 1f;
                if ((time /= duration * 0.5f) < 1f)
                    return 0.5f * (float)Math.Pow(2.0, 10f * (time - 1f));
                return 0.5f * (-(float)Math.Pow(2.0, -10f * --time) + 2f);
            case DotweenEase.InCirc:
                return -((float)Math.Sqrt(1f - (time /= duration) * time) - 1f);
            case DotweenEase.OutCirc:
                return (float)Math.Sqrt(1f - (time = time / duration - 1f) * time);
            case DotweenEase.InOutCirc:
                if ((time /= duration * 0.5f) < 1f)
                    return -0.5f * ((float)Math.Sqrt(1f - time * time) - 1f);
                return 0.5f * ((float)Math.Sqrt(1f - (time -= 2f) * time) + 1f);
            case DotweenEase.InElastic:
            {
                if (time == 0f)
                    return 0f;
                if ((time /= duration) == 1f)
                    return 1f;
                if (period == 0f)
                    period = duration * 0.3f;
                float shift = ElasticShift(ref overshootOrAmplitude, period);
                return -(overshootOrAmplitude * (float)Math.Pow(2.0, 10f * (time -= 1f)) *
                         (float)Math.Sin((time * duration - shift) * TwoPi / period));
            }
            case DotweenEase.OutElastic:
            {
                if (time == 0f)
                    return 0f;
                if ((time /= duration) == 1f)
                    return 1f;
                if (period == 0f)
                    period = duration * 0.3f;
                float shift = ElasticShift(ref overshootOrAmplitude, period);
                return overshootOrAmplitude * (float)Math.Pow(2.0, -10f * time) *
                       (float)Math.Sin((time * duration - shift) * TwoPi / period) + 1f;
            }
            case DotweenEase.InOutElastic:
            {
                if (time == 0f)
                    return 0f;
                if ((time /= duration * 0.5f) == 2f)
                    return 1f;
                if (period == 0f)
                    period = duration * (0.3f * 1.5f);
                float shift = ElasticShift(ref overshootOrAmplitude, period);
                if (time < 1f)
                {
                    return -0.5f * (overshootOrAmplitude * (float)Math.Pow(2.0, 10f * (time -= 1f)) *
                                    (float)Math.Sin((time * duration - shift) * TwoPi / period));
                }
                return overshootOrAmplitude * (float)Math.Pow(2.0, -10f * (time -= 1f)) *
                       (float)Math.Sin((time * duration - shift) * TwoPi / period) * 0.5f + 1f;
            }
            case DotweenEase.InBack:
                return (time /= duration) * time * ((overshootOrAmplitude + 1f) * time - overshootOrAmplitude);
            case DotweenEase.OutBack:
                return (time = time / duration - 1f) * time *
                       ((overshootOrAmplitude + 1f) * time + overshootOrAmplitude) + 1f;
            case DotweenEase.InOutBack:
                if ((time /= duration * 0.5f) < 1f)
                {
                    return 0.5f * (time * time *
                                   (((overshootOrAmplitude *= 1.525f) + 1f) * time - overshootOrAmplitude));
                }
                return 0.5f * ((time -= 2f) * time *
                               (((overshootOrAmplitude *= 1.525f) + 1f) * time + overshootOrAmplitude) + 2f);
            case DotweenEase.InBounce:
                return BounceEaseIn(time, duration);
            case DotweenEase.OutBounce:
                return BounceEaseOut(time, duration);
            case DotweenEase.InOutBounce:
                if (time < duration * 0.5f)
                    return BounceEaseIn(time * 2f, duration) * 0.5f;
                return BounceEaseOut(time * 2f - duration, duration) * 0.5f + 0.5f;
            case DotweenEase.Flash:
            case DotweenEase.InFlash:
            case DotweenEase.OutFlash:
            case DotweenEase.InOutFlash:
                return FlashEase(ease, time, duration, overshootOrAmplitude, period);
            case DotweenEase.INTERNAL_Zero:
                return 1f;
            default:
                // Unset and undefined values: the switch default is OutQuad.
                return -(time /= duration) * (time - 2f);
        }
    }

    private static float ElasticShift(ref float overshootOrAmplitude, float period)
    {
        if (overshootOrAmplitude < 1f)
        {
            overshootOrAmplitude = 1f;
            return period / 4f;
        }
        return period / TwoPi * (float)Math.Asin(1f / overshootOrAmplitude);
    }

    private static float BounceEaseIn(float time, float duration) =>
        1f - BounceEaseOut(duration - time, duration);

    private static float BounceEaseOut(float time, float duration)
    {
        if ((time /= duration) < 0.36363637f)
            return 7.5625f * time * time;
        if (time < 0.72727275f)
            return 7.5625f * (time -= 0.54545456f) * time + 0.75f;
        if (time < 0.90909094f)
            return 7.5625f * (time -= 0.8181818f) * time + 0.9375f;
        return 7.5625f * (time -= 0.95454544f) * time + 0.984375f;
    }

    private static float FlashEase(
        DotweenEase ease,
        float time,
        float duration,
        float overshootOrAmplitude,
        float period)
    {
        int stepIndex = (int)Math.Ceiling(time / duration * overshootOrAmplitude);
        float stepDuration = duration / overshootOrAmplitude;
        time -= stepDuration * (stepIndex - 1);
        float dir = stepIndex % 2 != 0 ? 1f : -1f;
        if (dir < 0f)
            time -= stepDuration;

        float res;
        switch (ease)
        {
            case DotweenEase.Flash:
                res = time * dir / stepDuration;
                break;
            case DotweenEase.InFlash:
                time *= dir;
                res = (time /= stepDuration) * time;
                break;
            case DotweenEase.OutFlash:
                time *= dir;
                res = -(time /= stepDuration) * (time - 2f);
                break;
            default:
                time *= dir;
                res = (time /= stepDuration * 0.5f) < 1f
                    ? 0.5f * time * time
                    : -0.5f * ((--time) * (time - 2f) - 1f);
                break;
        }
        return FlashWeightedEase(overshootOrAmplitude, period, stepIndex, dir, res);
    }

    private static float FlashWeightedEase(
        float overshootOrAmplitude,
        float period,
        int stepIndex,
        float dir,
        float res)
    {
        float easedRes = 0f;
        float finalDecimals = 0f;
        if (dir > 0f && (int)overshootOrAmplitude % 2 == 0)
            stepIndex++;
        else if (dir < 0f && (int)overshootOrAmplitude % 2 != 0)
            stepIndex++;

        if (period > 0f)
        {
            float finalTruncated = (float)Math.Truncate(overshootOrAmplitude);
            finalDecimals = overshootOrAmplitude - finalTruncated;
            if (finalTruncated % 2f > 0f)
                finalDecimals = 1f - finalDecimals;
            finalDecimals = finalDecimals * stepIndex / overshootOrAmplitude;
            easedRes = res * (overshootOrAmplitude - stepIndex) / overshootOrAmplitude;
        }
        else if (period < 0f)
        {
            period = -period;
            easedRes = res * stepIndex / overshootOrAmplitude;
        }

        float diff = easedRes - res;
        res += diff * period + finalDecimals;
        if (res > 1f)
            res = 1f;
        return res;
    }
}

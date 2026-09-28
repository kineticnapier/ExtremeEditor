namespace ExtremeEditor.Core;

internal static class DotweenEaseEvaluator
{
    private enum SupportedEase : byte
    {
        Linear,
        InSine,
        OutSine,
        InOutSine,
        InQuad,
        OutQuad,
        InOutQuad,
        InCubic,
        OutCubic,
        InOutCubic
    }

    public static bool IsSupported(string? ease) => TryResolve(ease, out _);

    public static bool TryEvaluate(
        string? ease,
        double elapsedSeconds,
        double durationSeconds,
        out float easedProgress)
    {
        if (!TryResolve(ease, out SupportedEase resolved))
        {
            easedProgress = 0;
            return false;
        }

        if (elapsedSeconds <= 0)
        {
            easedProgress = 0;
            return true;
        }
        if (elapsedSeconds >= durationSeconds)
        {
            easedProgress = 1;
            return true;
        }

        // DOTween's EaseManager receives float time and duration. Cast before
        // evaluating so the polynomial and trigonometric paths use the same
        // precision and operation order as the bundled runtime.
        float time = (float)elapsedSeconds;
        float duration = (float)durationSeconds;
        easedProgress = resolved switch
        {
            SupportedEase.Linear => time / duration,
            SupportedEase.InSine =>
                0f - (float)Math.Cos(time / duration * ((float)Math.PI / 2f)) + 1f,
            SupportedEase.OutSine =>
                (float)Math.Sin(time / duration * ((float)Math.PI / 2f)),
            SupportedEase.InOutSine =>
                -0.5f * ((float)Math.Cos((float)Math.PI * time / duration) - 1f),
            SupportedEase.InQuad => EvaluateInQuad(time, duration),
            SupportedEase.OutQuad => EvaluateOutQuad(time, duration),
            SupportedEase.InOutQuad => EvaluateInOutQuad(time, duration),
            SupportedEase.InCubic => EvaluateInCubic(time, duration),
            SupportedEase.OutCubic => EvaluateOutCubic(time, duration),
            SupportedEase.InOutCubic => EvaluateInOutCubic(time, duration),
            _ => 0
        };
        return true;
    }

    private static bool TryResolve(string? ease, out SupportedEase resolved)
    {
        switch (ease)
        {
            case null:
            case "":
            case "Linear":
                resolved = SupportedEase.Linear;
                return true;
            case "InSine":
                resolved = SupportedEase.InSine;
                return true;
            case "OutSine":
                resolved = SupportedEase.OutSine;
                return true;
            case "InOutSine":
                resolved = SupportedEase.InOutSine;
                return true;
            case "InQuad":
                resolved = SupportedEase.InQuad;
                return true;
            case "OutQuad":
                resolved = SupportedEase.OutQuad;
                return true;
            case "InOutQuad":
                resolved = SupportedEase.InOutQuad;
                return true;
            case "InCubic":
                resolved = SupportedEase.InCubic;
                return true;
            case "OutCubic":
                resolved = SupportedEase.OutCubic;
                return true;
            case "InOutCubic":
                resolved = SupportedEase.InOutCubic;
                return true;
            default:
                resolved = default;
                return false;
        }
    }

    private static float EvaluateInQuad(float time, float duration)
    {
        time /= duration;
        return time * time;
    }

    private static float EvaluateOutQuad(float time, float duration)
    {
        time /= duration;
        return -time * (time - 2f);
    }

    private static float EvaluateInOutQuad(float time, float duration)
    {
        time /= duration * 0.5f;
        if (time < 1f)
            return 0.5f * time * time;
        --time;
        return -0.5f * (time * (time - 2f) - 1f);
    }

    private static float EvaluateInCubic(float time, float duration)
    {
        time /= duration;
        return time * time * time;
    }

    private static float EvaluateOutCubic(float time, float duration)
    {
        time = time / duration - 1f;
        return time * time * time + 1f;
    }

    private static float EvaluateInOutCubic(float time, float duration)
    {
        time /= duration * 0.5f;
        if (time < 1f)
            return 0.5f * time * time * time;
        time -= 2f;
        return 0.5f * (time * time * time + 2f);
    }
}

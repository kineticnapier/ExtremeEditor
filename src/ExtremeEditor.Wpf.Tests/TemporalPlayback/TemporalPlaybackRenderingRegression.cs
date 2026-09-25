using System.Numerics;
using System.Reflection;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class TemporalPlaybackRenderingRegression
{
    public static void Run()
    {
        VerifyPlaybackFloorVisualExistsBetweenSceneAndPlanets();
        VerifyViewportCullingMargin();
        VerifyTemporalDiagnosticsExist();
    }

    private static void VerifyPlaybackFloorVisualExistsBetweenSceneAndPlanets()
    {
        PropertyInfo countProperty = typeof(LevelViewport).GetProperty(
            "VisualChildrenCount",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("VisualChildrenCount missing.");

        var viewport = new LevelViewport();
        int count = (int)(countProperty.GetValue(viewport)
            ?? throw new InvalidOperationException("VisualChildrenCount returned null."));
        if (count != 3)
        {
            throw new InvalidOperationException(
                $"Temporal playback requires scene + playback floors + planet overlay visuals. actual={count}.");
        }
    }

    private static void VerifyViewportCullingMargin()
    {
        MethodInfo cull = typeof(LevelViewport).GetMethod(
            "IntersectsPlaybackViewport",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LevelViewport.IntersectsPlaybackViewport is missing.");

        var viewport = new WorldRect(-10, -10, 10, 10);
        bool edge = (bool)(cull.Invoke(null, [new Vector2(11.0f, 0), viewport, 1.5f])
            ?? throw new InvalidOperationException("IntersectsPlaybackViewport returned null for edge case."));
        bool far = (bool)(cull.Invoke(null, [new Vector2(20.0f, 0), viewport, 1.5f])
            ?? throw new InvalidOperationException("IntersectsPlaybackViewport returned null for far case."));

        if (!edge || far)
        {
            throw new InvalidOperationException(
                $"Playback viewport culling margin is wrong. edge={edge}, far={far}.");
        }
    }

    private static void VerifyTemporalDiagnosticsExist()
    {
        string[] properties =
        [
            "TemporalPlaybackCandidateCount",
            "TemporalPlaybackVisibleFloorCount",
            "TemporalPlaybackVisibleIconCount",
            "TemporalPlaybackDrawMilliseconds",
            "TemporalPlaybackActive"
        ];

        foreach (string propertyName in properties)
        {
            if (typeof(LevelViewport).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public) is null)
                throw new InvalidOperationException($"LevelViewport.{propertyName} is missing.");
        }
    }
}

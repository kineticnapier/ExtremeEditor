using System.IO;
using System.Reflection;
using System.Windows;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class PlaybackStallDiagnosticsRegression
{
    public static void Run()
    {
        VerifyDiagnosticLoggerWritesConsoleAndFile();
        VerifyGapDiagnosticsAreExposed();
        VerifyLateAdmissionsAreCountedOnce();
    }

    private static void VerifyDiagnosticLoggerWritesConsoleAndFile()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        Type loggerType = assembly.GetType("ExtremeEditor.Wpf.PlaybackDiagnosticLogger")
            ?? throw new InvalidOperationException("PlaybackDiagnosticLogger is missing.");

        ConstructorInfo constructor = loggerType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [typeof(TextWriter), typeof(string)],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "PlaybackDiagnosticLogger(TextWriter, string) is missing.");

        MethodInfo logMethod = loggerType.GetMethod(
            "Log",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [typeof(string)],
            modifiers: null)
            ?? throw new InvalidOperationException("PlaybackDiagnosticLogger.Log(string) is missing.");

        string path = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-playback-diagnostics-{Guid.NewGuid():N}.log");
        var console = new StringWriter();
        object? logger = null;

        try
        {
            logger = constructor.Invoke([console, path]);
            logMethod.Invoke(logger, ["diagnostic-probe-line"]);

            if (logger is not IDisposable disposable)
                throw new InvalidOperationException("PlaybackDiagnosticLogger must be disposable so queued log writes can flush.");
            disposable.Dispose();
            logger = null;

            if (!console.ToString().Contains("diagnostic-probe-line", StringComparison.Ordinal))
                throw new InvalidOperationException("Playback diagnostics must be written to the console sink.");

            if (!File.Exists(path) ||
                !File.ReadAllText(path).Contains("diagnostic-probe-line", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Playback diagnostics must be written to the file sink.");
            }
        }
        finally
        {
            if (logger is IDisposable disposable)
                disposable.Dispose();
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void VerifyGapDiagnosticsAreExposed()
    {
        RequireProperty(typeof(MainWindow), "PlaybackTickMaxGapMilliseconds", typeof(double));
        RequireProperty(typeof(MainWindow), "PlaybackRenderMaxGapMilliseconds", typeof(double));

        var window = new MainWindow();
        try
        {
            string snapshot = window.PlaybackDiagnosticsSnapshot;
            foreach (string token in new[] { "tickMaxGap=", "renderMaxGap=", "native=" })
            {
                if (!snapshot.Contains(token, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Playback diagnostics snapshot is missing '{token}'. snapshot='{snapshot}'.");
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void VerifyLateAdmissionsAreCountedOnce()
    {
        PropertyInfo lateAdmissionCount = RequireProperty(
            typeof(LevelViewport),
            "TemporalPlaybackLateAdmissionCount",
            typeof(int));

        MethodInfo renderTemporal = typeof(LevelViewport).GetMethod(
            "RenderTemporalPlaybackFloors",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "LevelViewport.RenderTemporalPlaybackFloors is missing.");

        var level = new LevelDocument
        {
            SourcePath = "<late-admission-regression>",
            Angles = [],
            Positions = [System.Numerics.Vector2.Zero],
            ActionCount = 0,
            ActionTypeCounts = new Dictionary<string, int>(),
            ActionsByFloor = new Dictionary<int, LevelAction[]>(),
            InitialBpm = 120.0,
            SongFilename = null,
            OffsetMilliseconds = 0,
            PitchPercent = 100,
            CountdownTicks = 0,
            SeparateCountdownTime = false,
            DefaultHitSound = "Kick",
            HitSoundVolumePercent = 100,
            Bounds = PathBuilder.CalculateBounds([System.Numerics.Vector2.Zero])
        };

        var timingMap = new TimingMap(
        [
            new FloorTiming(0, 0.1, 0.11, 0, 0, Math.PI, 120, false, false)
        ]);

        var viewport = new LevelViewport();
        viewport.Measure(new Size(800, 600));
        viewport.Arrange(new Rect(0, 0, 800, 600));
        viewport.SetLevel(level, new SpatialGridIndex(level.Positions));

        try
        {
            renderTemporal.Invoke(viewport, [timingMap, 0.1]);
            int afterFirstAdmission = (int)(lateAdmissionCount.GetValue(viewport)
                ?? throw new InvalidOperationException("TemporalPlaybackLateAdmissionCount returned null."));
            if (afterFirstAdmission != 1)
            {
                throw new InvalidOperationException(
                    $"A floor first admitted while already visible must count as a late admission. actual={afterFirstAdmission}.");
            }

            renderTemporal.Invoke(viewport, [timingMap, 0.1]);
            int afterRetainedFrame = (int)(lateAdmissionCount.GetValue(viewport)
                ?? throw new InvalidOperationException("TemporalPlaybackLateAdmissionCount returned null."));
            if (afterRetainedFrame != 1)
                throw new InvalidOperationException("An already-retained floor must not be counted as a new late admission again.");
        }
        finally
        {
            viewport.ShutdownRasterWorker();
        }
    }

    private static PropertyInfo RequireProperty(Type type, string name, Type propertyType)
    {
        PropertyInfo? property = type.GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (property is null)
            throw new InvalidOperationException($"{type.Name}.{name} is missing.");
        if (property.PropertyType != propertyType)
            throw new InvalidOperationException(
                $"{type.Name}.{name} must be {propertyType.Name}, actual={property.PropertyType.Name}.");
        return property;
    }
}

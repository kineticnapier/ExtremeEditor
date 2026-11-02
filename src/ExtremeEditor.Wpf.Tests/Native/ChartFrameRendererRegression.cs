using System.IO;
using System.Reflection;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class ChartFrameRendererRegression
{
    public static void Run()
    {
        VerifyHeadlessDimensionLimits();
        VerifyCleanViewPersistenceContract();
        VerifyRejectedHeadlessRenderDoesNotAllocateFrame();
        VerifyRejectedBorrowedRenderDoesNotAllocateFrame();

        string root = Path.Combine(Path.GetTempPath(), "ExtremeEditor.HeadlessRegression", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string chartPath = Path.Combine(root, "smoke.adofai");
        string pngPath = Path.Combine(root, "frame.png");

        try
        {
            File.WriteAllText(
                chartPath,
                """
                {
                  "angleData": [0, 90, 180, 270],
                  "settings": {
                    "bpm": 120,
                    "pitch": 100,
                    "countdownTicks": 1
                  },
                  "actions": [
                    { "floor": 1, "eventType": "Twirl" },
                    { "floor": 2, "eventType": "SetSpeed", "speedType": "Multiplier", "bpmMultiplier": 1.5 }
                  ]
                }
                """);

            using var renderer = new NativeChartFrameRenderer();
            renderer.Load(chartPath);

            RenderedFrame first = renderer.Render(0.25);
            RenderedFrame same = renderer.Render(0.25);
            if (first.Width != 320 || first.Height != 180)
                throw new InvalidOperationException("Headless facade did not produce the default 320x180 frame.");
            if (first.Rgb.Length != 320 * 180 * 3)
                throw new InvalidOperationException("Headless facade did not return tightly packed RGB888 data.");
            if (!first.Rgb.AsSpan().SequenceEqual(same.Rgb))
                throw new InvalidOperationException("Repeated facade renders at the same chart time were not deterministic.");

            renderer.Reset();
            RenderedFrame reset = renderer.Render(0.25);
            if (!first.Rgb.AsSpan().SequenceEqual(reset.Rgb))
                throw new InvalidOperationException("Reset changed deterministic frame output.");

            // A Reset replaces the native session, but must preserve the
            // requested clean view used by headless export callers.
            renderer.SetCleanView(true);
            RenderedFrame cleanBeforeReset = renderer.Render(0.25);
            renderer.Reset();
            RenderedFrame cleanAfterReset = renderer.Render(0.25);
            if (!cleanBeforeReset.Rgb.AsSpan().SequenceEqual(cleanAfterReset.Rgb))
                throw new InvalidOperationException("Reset changed clean-view frame output.");

            ChartFramePngWriter.Save(first, pngPath);
            byte[] png = File.ReadAllBytes(pngPath);
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (png.Length <= signature.Length || !png.AsSpan(0, signature.Length).SequenceEqual(signature))
                throw new InvalidOperationException("PNG smoke export did not produce a valid PNG signature.");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup only; the regression result must not depend on temp cleanup.
            }
        }
    }

    private static void VerifyCleanViewPersistenceContract()
    {
        // Rendering alone may not reveal a lost clean-view flag if no floor is
        // selected. Verify that both session replacement paths reapply it.
        DirectoryInfo? root = new(Environment.CurrentDirectory);
        while (root is not null &&
               !File.Exists(Path.Combine(root.FullName, "src", "ExtremeEditor.Wpf",
                   "Native", "NativeChartFrameRenderer.cs")))
            root = root.Parent;
        if (root is null)
            throw new InvalidOperationException("Could not locate NativeChartFrameRenderer source.");

        string source = File.ReadAllText(Path.Combine(root.FullName, "src",
            "ExtremeEditor.Wpf", "Native", "NativeChartFrameRenderer.cs"));
        int load = source.IndexOf("public void Load(LevelDocument level, TimingMap timingMap)", StringComparison.Ordinal);
        int setClean = source.IndexOf("public void SetCleanView(bool enabled)", StringComparison.Ordinal);
        int reset = source.IndexOf("public void Reset()", StringComparison.Ordinal);
        int dispose = source.IndexOf("public void Dispose()", StringComparison.Ordinal);
        if (load < 0 || setClean <= load || reset <= setClean || dispose <= reset ||
            !source[load..setClean].Contains("next.SetCleanView(_cleanView);", StringComparison.Ordinal) ||
            !source[setClean..reset].Contains("_cleanView = enabled;", StringComparison.Ordinal) ||
            !source[reset..dispose].Contains("next.SetCleanView(_cleanView);", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Headless Load and Reset must preserve the requested clean-view mode.");
        }

        // Both replacement paths create a native handle before setting the
        // retained view mode. A failed configuration must dispose that new
        // handle while leaving the old session installed.
        foreach (string section in new[] { source[load..setClean], source[reset..dispose] })
        {
            int configure = section.IndexOf("next.SetCleanView(_cleanView);", StringComparison.Ordinal);
            int catchBlock = section.IndexOf("catch", configure, StringComparison.Ordinal);
            int cleanup = section.IndexOf("next.Dispose();", StringComparison.Ordinal);
            int rethrow = section.IndexOf("throw;", StringComparison.Ordinal);
            int swap = section.IndexOf("_session = next;", StringComparison.Ordinal);
            if (configure < 0 || catchBlock <= configure || cleanup <= catchBlock ||
                rethrow <= cleanup || swap <= rethrow ||
                !section.Contains("try", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Headless replacement must dispose a failed new session before swapping.");
            }
        }
    }

    private static void VerifyRejectedHeadlessRenderDoesNotAllocateFrame()
    {
        // A disposed managed shell needs no native handle or large allocation.
        // The 1080p dimensions make a misplaced allocation visible in this test.
        ConstructorInfo? constructor = typeof(NativeHeadlessRendererSession).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(nint), typeof(int), typeof(int)],
            modifiers: null);
        if (constructor is null)
            throw new InvalidOperationException("Headless session constructor was not found.");

        var disposed = (NativeHeadlessRendererSession)constructor.Invoke(
            [nint.Zero, 1920, 1080]);
        // Warm up exception/JIT paths so the allocation check measures RGB
        // frame allocation rather than one-time runtime initialization.
        try
        {
            _ = disposed.Render(0.0, 0.0);
            throw new InvalidOperationException("Disposed render unexpectedly succeeded.");
        }
        catch (ObjectDisposedException)
        {
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool rejected = false;
        try
        {
            _ = disposed.Render(0.0, 0.0);
        }
        catch (ObjectDisposedException)
        {
            rejected = true;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (!rejected || allocated >= 1_000_000)
            throw new InvalidOperationException(
                $"Disposed headless render must fail before allocating RGB24 data (allocated {allocated} bytes).");
    }


    private static void VerifyRejectedBorrowedRenderDoesNotAllocateFrame()
    {
        // No native renderer is needed: a disposed shell rejects the request.
        // In particular, the first borrowed call must not cache a 1080p RGB array.
        ConstructorInfo? constructor = typeof(NativeHeadlessRendererSession).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(nint), typeof(int), typeof(int)],
            modifiers: null);
        FieldInfo? sessionField = typeof(NativeChartFrameRenderer).GetField(
            "_session", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo? bufferField = typeof(NativeChartFrameRenderer).GetField(
            "_reusableRgb", BindingFlags.Instance | BindingFlags.NonPublic);
        if (constructor is null || sessionField is null || bufferField is null)
            throw new InvalidOperationException("Borrowed frame test setup is missing.");

        using var renderer = new NativeChartFrameRenderer(1920, 1080);
        var disposed = (NativeHeadlessRendererSession)constructor.Invoke(
            [nint.Zero, 1920, 1080]);
        sessionField.SetValue(renderer, disposed);

        // Warm up exception/JIT paths before measuring managed allocation.
        try
        {
            _ = renderer.RenderBorrowed(0.0, 0.0);
            throw new InvalidOperationException("Disposed borrowed render unexpectedly succeeded.");
        }
        catch (ObjectDisposedException)
        {
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool rejected = false;
        try
        {
            _ = renderer.RenderBorrowed(0.0, 0.0);
        }
        catch (ObjectDisposedException)
        {
            rejected = true;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (!rejected || allocated >= 1_000_000 || bufferField.GetValue(renderer) is not null)
            throw new InvalidOperationException(
                $"Rejected borrowed render must not cache RGB24 data (allocated {allocated} bytes).");
    }

    private static void VerifyHeadlessDimensionLimits()
    {
        // Avoid calling native CreateHeadless with huge dimensions even if
        // this validation regresses; only the pure guard is exercised here.
        NativeHeadlessRendererSession.ValidateFrameDimensions(1920, 1080);
        ExpectHeadlessDimensionRejected(0, 1);
        ExpectHeadlessDimensionRejected(1, 0);
        ExpectHeadlessDimensionRejected(int.MaxValue, 1);
        ExpectHeadlessDimensionRejected(1, int.MaxValue);
    }

    private static void ExpectHeadlessDimensionRejected(int width, int height)
    {
        try
        {
            NativeHeadlessRendererSession.ValidateFrameDimensions(width, height);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Headless renderer accepted an impossible RGB24 frame: {width}x{height}.");
    }
}

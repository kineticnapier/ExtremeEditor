using System.IO;
using System.Reflection;
using System.Threading;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Export;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class VideoExportFramePipelineRegression
{
    public static void Run()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        Type planType = assembly.GetType("ExtremeEditor.Wpf.Export.VideoExportPlan")
            ?? throw new InvalidOperationException("VE1 VideoExportPlan is missing.");
        Type frameType = assembly.GetType("ExtremeEditor.Wpf.Export.VideoExportFrame")
            ?? throw new InvalidOperationException("VE1 VideoExportFrame is missing.");
        Type pipelineType = assembly.GetType("ExtremeEditor.Wpf.Export.VideoExportFramePipeline")
            ?? throw new InvalidOperationException(
                "VE2 VideoExportFramePipeline is missing.");

        Type nativeRendererType = typeof(NativeChartFrameRenderer);
        Type rendererContract = nativeRendererType.GetInterfaces()
            .SingleOrDefault(type =>
                string.Equals(type.Name, "IChartFrameRenderer", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "NativeChartFrameRenderer must expose IChartFrameRenderer for VE2.");

        MethodInfo renderMethod = rendererContract.GetMethod(
            "Render",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: [typeof(double), typeof(double)],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "IChartFrameRenderer.Render(sceneTimeSeconds, visualTimeSeconds) is missing.");
        Type renderedFrameType = renderMethod.ReturnType;

        ConstructorInfo constructor = pipelineType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [rendererContract],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "VideoExportFramePipeline must accept IChartFrameRenderer so VE2 is testable without owning a native renderer.");

        Type consumerType = typeof(Func<,,,>).MakeGenericType(
            frameType,
            renderedFrameType,
            typeof(CancellationToken),
            typeof(ValueTask));

        MethodInfo renderAsync = pipelineType.GetMethod(
            "RenderAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [planType, consumerType, typeof(CancellationToken)],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "VideoExportFramePipeline.RenderAsync(VideoExportPlan, frame consumer, CancellationToken) is missing.");

        if (renderAsync.ReturnType != typeof(ValueTask))
        {
            throw new InvalidOperationException(
                "VE2 RenderAsync must return ValueTask and stream frames to the consumer instead of returning an in-memory frame collection.");
        }

        ParameterInfo[] parameters = renderAsync.GetParameters();
        if (!string.Equals(parameters[2].Name, "cancellationToken", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "VE2 RenderAsync must expose an explicit cancellationToken.");
        }

        VerifySourceContract();
        VerifyBorrowedFrameAwaitedBeforeReuse().GetAwaiter().GetResult();
    }


    // The borrowed RGB array is shared between frames. An asynchronous
    // consumer must finish using it before the pipeline requests another.
    private static async Task VerifyBorrowedFrameAwaitedBeforeReuse()
    {
        using var renderer = new BorrowedFrameProbeRenderer();
        var pipeline = new VideoExportFramePipeline(renderer);
        var plan = new VideoExportPlan(0.0, 2.0, 1, 1, 1);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int consumed = 0;

        async ValueTask Consume(VideoExportFrame frame, RenderedFrame rendered, CancellationToken token)
        {
            if (frame.Index == 0)
            {
                if (rendered.Rgb[0] != 1)
                    throw new InvalidOperationException("First borrowed RGB frame was incorrect.");

                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);

                if (rendered.Rgb[0] != 1)
                    throw new InvalidOperationException("Borrowed RGB was overwritten before the consumer finished.");
            }
            else if (frame.Index == 1 && rendered.Rgb[0] != 2)
            {
                throw new InvalidOperationException("Second borrowed RGB frame was incorrect.");
            }

            consumed++;
        }

        Task run = pipeline.RenderAsync(plan, Consume, CancellationToken.None).AsTask();
        try
        {
            Task first = await Task.WhenAny(entered.Task, run)
                .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (first == run)
                await run.ConfigureAwait(false);
            if (renderer.BorrowedCalls != 1)
                throw new InvalidOperationException("Pipeline rendered the next borrowed frame while its consumer was suspended.");
        }
        finally
        {
            release.TrySetResult(true);
        }

        await run.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (renderer.BorrowedCalls != 2 || renderer.OwnedCalls != 0 || consumed != 2)
            throw new InvalidOperationException("Pipeline did not consume exactly two borrowed frames in order.");
    }

    private sealed class BorrowedFrameProbeRenderer : IChartFrameRenderer, IBorrowedChartFrameRenderer
    {
        private readonly byte[] _rgb = new byte[3];
        internal int BorrowedCalls { get; private set; }
        internal int OwnedCalls { get; private set; }

        public void Load(string adofaiPath) => throw new NotSupportedException();
        public void Reset() => throw new NotSupportedException();
        public RenderedFrame Render(double chartTimeSeconds) =>
            Render(chartTimeSeconds, chartTimeSeconds);

        public RenderedFrame Render(double sceneTimeSeconds, double visualTimeSeconds)
        {
            OwnedCalls++;
            throw new InvalidOperationException("The pipeline used the allocating render path.");
        }

        public RenderedFrame RenderBorrowed(double sceneTimeSeconds, double visualTimeSeconds)
        {
            _rgb[0] = checked((byte)++BorrowedCalls);
            return new RenderedFrame(1, 1, _rgb);
        }

        public void Dispose() { }
    }

    private static void VerifySourceContract()
    {
        string root = FindRepoRoot();
        string path = Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "Export", "VideoExportFramePipeline.cs");
        if (!File.Exists(path))
            throw new InvalidOperationException("VE2 production source is missing.");

        string source = File.ReadAllText(path);
        var failures = new List<string>();

        if (!source.Contains("VideoExportTimeline.Enumerate", StringComparison.Ordinal))
            failures.Add("pipeline must lazily consume the deterministic VE1 timeline");
        if (source.Contains("VideoExportTimeline.Build", StringComparison.Ordinal))
            failures.Add("pipeline must not allocate a complete frame list");
        if (!source.Contains(".ChartTimeSeconds", StringComparison.Ordinal) ||
            !source.Contains(".VisualTimeSeconds", StringComparison.Ordinal))
        {
            failures.Add(
                "pipeline must pass chart time and visual time separately to the frame renderer");
        }
        if (!source.Contains("ThrowIfCancellationRequested", StringComparison.Ordinal))
            failures.Add("pipeline must check cancellation while streaming");
        if (source.Contains("List<RenderedFrame>", StringComparison.Ordinal) ||
            source.Contains("RenderedFrame[]", StringComparison.Ordinal))
        {
            failures.Add(
                "pipeline must not accumulate all rendered RGB frames in memory");
        }
        if (!source.Contains("IBorrowedChartFrameRenderer", StringComparison.Ordinal) ||
            !source.Contains("RenderBorrowed", StringComparison.Ordinal))
        {
            failures.Add(
                "video pipeline must use the renderer's borrowed frame path so the RGB buffer is reused after each awaited consumer");
        }

        string renderer = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "Native", "NativeChartFrameRenderer.cs"));
        if (!renderer.Contains("IBorrowedChartFrameRenderer", StringComparison.Ordinal) ||
            !renderer.Contains("_reusableRgb", StringComparison.Ordinal) ||
            !renderer.Contains("RenderBorrowed", StringComparison.Ordinal))
        {
            failures.Add(
                "native video renderer must retain one reusable RGB buffer without changing the public owned-frame contract");
        }
        if (!renderer.Contains("NativeRgbFrame frame = session.Render", StringComparison.Ordinal) ||
            !renderer.Contains("session.RenderInto", StringComparison.Ordinal))
        {
            failures.Add(
                "public rendering must keep its owned allocation while only borrowed rendering writes into reusable storage");
        }

        string timeline = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "Export", "VideoExportTimeline.cs"));
        string service = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "Export", "VideoExportService.cs"));
        if (!timeline.Contains("CountFrames", StringComparison.Ordinal) ||
            !timeline.Contains("IEnumerable<VideoExportFrame>", StringComparison.Ordinal))
            failures.Add("timeline must expose allocation-free counting and lazy enumeration");
        if (service.Contains("VideoExportTimeline.Build", StringComparison.Ordinal))
            failures.Add("progress counting must not build every frame object");

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "VE2 frame pipeline contract failed:\n- " +
                string.Join("\n- ", failures));
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src", "ExtremeEditor.Wpf")))
                return current.FullName;
            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the ExtremeEditor repository root for VE2 regression.");
    }
}

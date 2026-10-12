using System.Reflection;
using System.Threading;
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

        if (!source.Contains("VideoExportTimeline.Build", StringComparison.Ordinal))
            failures.Add("pipeline must consume the deterministic VE1 timeline");
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

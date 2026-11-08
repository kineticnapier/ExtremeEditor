using System.IO;
using System.Reflection;

namespace ExtremeEditor.Wpf.Tests;

internal static class VideoExportEncoderRegression
{
    public static void Run()
    {
        Assembly assembly = typeof(MainWindow).Assembly;

        Type? encoderType = assembly.GetType("ExtremeEditor.Wpf.Export.IVideoEncoder");
        Type? startInfoType = assembly.GetType("ExtremeEditor.Wpf.Export.VideoEncoderStartInfo");
        Type? muxPipelineType = assembly.GetType("ExtremeEditor.Wpf.Export.VideoExportMuxPipeline");

        var missing = new List<string>();
        if (encoderType is null)
            missing.Add("IVideoEncoder is missing");
        if (startInfoType is null)
            missing.Add("VideoEncoderStartInfo is missing");
        if (muxPipelineType is null)
            missing.Add("VideoExportMuxPipeline is missing");

        if (missing.Count != 0)
        {
            throw new InvalidOperationException(
                "VE4 encoder contract is not available:\n- " +
                string.Join("\n- ", missing));
        }

        VerifyEncoderContract(encoderType!, startInfoType!);
        VerifyMuxPipelineContract(muxPipelineType!, encoderType!);
        VerifyCompletionRequiresVideoPhase();
        VerifySourceContract();
    }

    private static void VerifyEncoderContract(Type encoderType, Type startInfoType)
    {
        if (!encoderType.IsInterface)
            throw new InvalidOperationException("IVideoEncoder must be an interface.");

        RequireMethod(
            encoderType,
            "BeginAsync",
            typeof(ValueTask),
            [startInfoType, typeof(CancellationToken)]);

        RequireMethod(
            encoderType,
            "WriteVideoFrameAsync",
            typeof(ValueTask),
            [typeof(byte[]), typeof(CancellationToken)]);

        Type audioChunkType = typeof(ExtremeEditor.Audio.OfflineAudioChunk);
        RequireMethod(
            encoderType,
            "WriteAudioChunkAsync",
            typeof(ValueTask),
            [audioChunkType, typeof(CancellationToken)]);

        RequireMethod(
            encoderType,
            "CompleteAsync",
            typeof(ValueTask),
            [typeof(CancellationToken)]);
    }

    private static void VerifyMuxPipelineContract(Type pipelineType, Type encoderType)
    {
        ConstructorInfo? constructor = pipelineType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [encoderType],
            modifiers: null);

        if (constructor is null)
        {
            throw new InvalidOperationException(
                "VideoExportMuxPipeline must accept IVideoEncoder via constructor injection.");
        }

        MethodInfo? writeVideo = pipelineType.GetMethod(
            "WriteVideoFrameAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        MethodInfo? writeAudio = pipelineType.GetMethod(
            "WriteAudioChunkAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (writeVideo is null || writeVideo.ReturnType != typeof(ValueTask))
            throw new InvalidOperationException(
                "VideoExportMuxPipeline.WriteVideoFrameAsync is missing or not streaming.");

        if (writeAudio is null || writeAudio.ReturnType != typeof(ValueTask))
            throw new InvalidOperationException(
                "VideoExportMuxPipeline.WriteAudioChunkAsync is missing or not streaming.");
    }

    private static void RequireMethod(
        Type type,
        string name,
        Type returnType,
        Type[] parameterTypes)
    {
        MethodInfo? method = type.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: parameterTypes,
            modifiers: null);

        if (method is null || method.ReturnType != returnType)
        {
            throw new InvalidOperationException(
                $"{type.Name}.{name} has the wrong streaming contract.");
        }
    }

    private static void VerifyCompletionRequiresVideoPhase()
    {
        // The mux must reject premature completion itself, even if the
        // injected encoder would otherwise accept it.
        var encoder = new RecordingVideoEncoder();
        var mux = new VideoExportMuxPipeline(encoder);
        var startInfo = new VideoEncoderStartInfo(
            "phase-check.mp4", 2, 2, 30, 44100, 2);

        mux.BeginAsync(startInfo, CancellationToken.None).GetAwaiter().GetResult();

        bool rejected = false;
        try
        {
            mux.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("video phase", StringComparison.OrdinalIgnoreCase))
        {
            rejected = true;
        }

        if (!rejected || encoder.CompleteCalls != 0)
            throw new InvalidOperationException(
                "Mux completion before BeginVideoAsync must fail without calling the encoder.");

        // A rejected early completion must leave the mux usable.
        mux.BeginVideoAsync(CancellationToken.None).GetAwaiter().GetResult();
        mux.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();
        mux.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (encoder.CompleteCalls != 1)
            throw new InvalidOperationException(
                "Mux completion must be successful and idempotent after BeginVideoAsync.");
    }

    private sealed class RecordingVideoEncoder : IVideoEncoder
    {
        internal int CompleteCalls { get; private set; }

        public ValueTask BeginAsync(
            VideoEncoderStartInfo startInfo, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask WriteVideoFrameAsync(
            byte[] rgb, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask WriteAudioChunkAsync(
            ExtremeEditor.Audio.OfflineAudioChunk chunk, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask BeginVideoAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            CompleteCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void VerifySourceContract()
    {
        string root = FindRepoRoot();
        string pipelinePath = Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "Export", "VideoExportMuxPipeline.cs");
        if (!File.Exists(pipelinePath))
            throw new InvalidOperationException("VE4 mux pipeline source is missing.");

        string source = File.ReadAllText(pipelinePath);
        var failures = new List<string>();

        if (!source.Contains("IVideoEncoder", StringComparison.Ordinal))
            failures.Add("mux pipeline must depend on IVideoEncoder");
        if (!source.Contains("WriteVideoFrameAsync", StringComparison.Ordinal))
            failures.Add("mux pipeline must forward frames incrementally");
        if (!source.Contains("WriteAudioChunkAsync", StringComparison.Ordinal))
            failures.Add("mux pipeline must forward audio chunks incrementally");
        if (!source.Contains("ThrowIfCancellationRequested", StringComparison.Ordinal))
            failures.Add("mux pipeline must observe cancellation");
        if (source.Contains("List<RenderedFrame>", StringComparison.Ordinal) ||
            source.Contains("List<OfflineAudioChunk>", StringComparison.Ordinal))
        {
            failures.Add("mux pipeline must not accumulate full video/audio exports in memory");
        }

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "VE4 mux source contract failed:\n- " +
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
            "Could not locate the ExtremeEditor repository root for VE4 regression.");
    }
}

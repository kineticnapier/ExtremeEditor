using System.IO;
using System.Reflection;

namespace ExtremeEditor.Wpf.Tests;

internal static class VideoExportOfflineAudioRegression
{
    public static void Run()
    {
        Assembly audioAssembly = typeof(ExtremeEditor.Audio.AudioPlayer).Assembly;

        Type? graphFactory = audioAssembly.GetType("ExtremeEditor.Audio.AudioGraphFactory");
        Type? renderPlan = audioAssembly.GetType("ExtremeEditor.Audio.OfflineAudioRenderPlan");
        Type? audioChunk = audioAssembly.GetType("ExtremeEditor.Audio.OfflineAudioChunk");
        Type? renderer = audioAssembly.GetType("ExtremeEditor.Audio.OfflineAudioRenderer");

        var missing = new List<string>();
        if (graphFactory is null)
            missing.Add("AudioGraphFactory is missing");
        if (renderPlan is null)
            missing.Add("OfflineAudioRenderPlan is missing");
        if (audioChunk is null)
            missing.Add("OfflineAudioChunk is missing");
        if (renderer is null)
            missing.Add("OfflineAudioRenderer is missing");

        if (missing.Count != 0)
        {
            throw new InvalidOperationException(
                "VE3 offline audio contract is not available:\n- " +
                string.Join("\n- ", missing));
        }

        VerifyOfflineRendererApi(renderer!, renderPlan!, audioChunk!);
        VerifySharedGraphSourceContract();
    }

    private static void VerifyOfflineRendererApi(Type renderer, Type renderPlan, Type audioChunk)
    {
        Type consumerType = typeof(Func<,,,>).MakeGenericType(
            audioChunk,
            typeof(CancellationToken),
            typeof(ValueTask),
            typeof(ValueTask));

        MethodInfo[] methods = renderer.GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        MethodInfo? renderAsync = methods.SingleOrDefault(method =>
            string.Equals(method.Name, "RenderAsync", StringComparison.Ordinal) &&
            method.ReturnType == typeof(ValueTask) &&
            method.GetParameters().Length == 3 &&
            method.GetParameters()[0].ParameterType == renderPlan &&
            method.GetParameters()[2].ParameterType == typeof(CancellationToken));

        if (renderAsync is null)
        {
            throw new InvalidOperationException(
                "OfflineAudioRenderer.RenderAsync(OfflineAudioRenderPlan, chunk consumer, CancellationToken) is missing.");
        }

        Type actualConsumer = renderAsync.GetParameters()[1].ParameterType;
        if (!actualConsumer.IsGenericType ||
            actualConsumer.GetGenericTypeDefinition() != typeof(Func<,,,>))
        {
            throw new InvalidOperationException(
                "VE3 offline audio must stream chunks to a consumer instead of returning the complete PCM buffer.");
        }

        Type[] args = actualConsumer.GetGenericArguments();
        if (args[0] != audioChunk ||
            args[1] != typeof(CancellationToken) ||
            args[2] != typeof(ValueTask) ||
            args[3] != typeof(ValueTask))
        {
            throw new InvalidOperationException(
                "VE3 offline audio chunk consumer signature is not the expected streaming contract.");
        }
    }

    private static void VerifySharedGraphSourceContract()
    {
        string root = FindRepoRoot();
        string audioPlayerPath = Path.Combine(
            root, "src", "ExtremeEditor.Audio", "AudioPlayer.cs");
        string graphFactoryPath = Path.Combine(
            root, "src", "ExtremeEditor.Audio", "AudioGraphFactory.cs");
        string offlinePath = Path.Combine(
            root, "src", "ExtremeEditor.Audio", "OfflineAudioRenderer.cs");

        var failures = new List<string>();

        if (!File.Exists(graphFactoryPath))
        {
            failures.Add("shared AudioGraphFactory source is missing");
        }
        else
        {
            string graphFactory = File.ReadAllText(graphFactoryPath);
            if (!graphFactory.Contains("UnifiedAudioSampleProvider", StringComparison.Ordinal))
                failures.Add("shared graph factory must build the unified song/hitsound graph");
            if (!graphFactory.Contains("SampleAccurateHitSoundProvider", StringComparison.Ordinal))
                failures.Add("shared graph factory must use sample-accurate hitsounds");
        }

        string audioPlayer = File.ReadAllText(audioPlayerPath);
        if (!audioPlayer.Contains("AudioGraphFactory", StringComparison.Ordinal))
            failures.Add("realtime AudioPlayer must use the shared AudioGraphFactory");

        if (!File.Exists(offlinePath))
        {
            failures.Add("OfflineAudioRenderer source is missing");
        }
        else
        {
            string offline = File.ReadAllText(offlinePath);
            if (!offline.Contains("AudioGraphFactory", StringComparison.Ordinal))
                failures.Add("offline renderer must use the same AudioGraphFactory as realtime playback");
            if (offline.Contains("WaveOutEvent", StringComparison.Ordinal))
                failures.Add("offline renderer must not create a WaveOutEvent or depend on realtime device playback");
            if (!offline.Contains("ThrowIfCancellationRequested", StringComparison.Ordinal))
                failures.Add("offline renderer must observe cancellation while streaming PCM");
            if (offline.Contains("List<float>", StringComparison.Ordinal) ||
                offline.Contains("float[] all", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("offline renderer must not accumulate the complete PCM export in memory");
            }
        }

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "VE3 offline audio source contract failed:\n- " +
                string.Join("\n- ", failures));
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src", "ExtremeEditor.Audio")))
                return current.FullName;
            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the ExtremeEditor repository root for VE3 regression.");
    }
}

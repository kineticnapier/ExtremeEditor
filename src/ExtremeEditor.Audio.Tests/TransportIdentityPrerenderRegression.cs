using System.Reflection;
using System.Runtime.CompilerServices;
using ExtremeEditor.Audio;

namespace ExtremeEditor.Audio.Tests;

internal static class TransportIdentityPrerenderRegression
{
    [ModuleInitializer]
    public static void Run()
    {
        FieldInfo? sourcePcm = typeof(TransportHitSoundSampleProvider).GetField(
            "_sourcePcm",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (sourcePcm is null || sourcePcm.FieldType != typeof(SampleAccurateHitSoundProvider))
        {
            throw new InvalidOperationException(
                "Dense 1x playback must retain the prerendered SampleAccurateHitSoundProvider instead of remixing every active hit voice per output frame.");
        }

        MethodInfo? identityRead = typeof(TransportHitSoundSampleProvider).GetMethod(
            "ReadIdentitySourcePcm",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (identityRead is null)
        {
            throw new InvalidOperationException(
                "TransportHitSoundSampleProvider needs a block PCM fast path for identity (1x) playback.");
        }
    }
}

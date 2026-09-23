using System.Reflection;
using System.Runtime.CompilerServices;
using ExtremeEditor.Audio;

namespace ExtremeEditor.Audio.Tests;

internal static class AudioPcmCachePolicyRegression
{
    private const long MiB = 1024L * 1024L;
    private const long GiB = 1024L * MiB;

    [ModuleInitializer]
    public static void Run()
    {
        Assembly audioAssembly = typeof(AudioPlayer).Assembly;
        Type policyType = audioAssembly.GetType("ExtremeEditor.Audio.AudioPcmCachePolicy")
            ?? throw new InvalidOperationException(
                "RED: ExtremeEditor.Audio.AudioPcmCachePolicy does not exist.");

        MethodInfo calculateBudget = policyType.GetMethod(
            "CalculateAutoBudgetBytes",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(long)],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "RED: AudioPcmCachePolicy.CalculateAutoBudgetBytes(long) does not exist.");

        MethodInfo calculatePrimeFrames = policyType.GetMethod(
            "CalculateInitialPrimeFrames",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(int)],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "RED: AudioPcmCachePolicy.CalculateInitialPrimeFrames(int) does not exist.");

        Equal(256L * MiB / 5, InvokeLong(calculateBudget, 256L * MiB), "256 MiB available RAM budget");
        Equal(1L * GiB / 5, InvokeLong(calculateBudget, 1L * GiB), "1 GiB available RAM budget");
        Equal(4L * GiB / 5, InvokeLong(calculateBudget, 4L * GiB), "4 GiB available RAM budget");
        Equal(8L * GiB, InvokeLong(calculateBudget, 64L * GiB), "64 GiB available RAM budget cap");
        Equal(24_000L, InvokeLong(calculatePrimeFrames, 48_000), "48 kHz initial prime frames");
        Equal(500L, InvokeLong(calculatePrimeFrames, 1_000), "1 kHz initial prime frames");
    }

    private static long InvokeLong(MethodInfo method, params object[] arguments)
    {
        object? result = method.Invoke(null, arguments);
        return result switch
        {
            long value => value,
            int value => value,
            _ => throw new InvalidOperationException($"{method.Name} must return an integer frame/byte count.")
        };
    }

    private static void Equal(long expected, long actual, string name)
    {
        if (expected != actual)
            throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
    }
}

using System.Collections.Concurrent;

namespace ExtremeEditor.Core;

public static class ShutdownDiagnostics
{
    public const string EnvironmentVariable = "EXTREMEEDITOR_SHUTDOWN_DIAGNOSTICS";

    private static readonly ConcurrentDictionary<string, byte> LoggedPhases =
        new(StringComparer.Ordinal);
    private static readonly object WriteGate = new();
    private static string _lastCompletedPhase = "none";
    private static int _active;

    public static bool Enabled { get; } =
        string.Equals(
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            "1",
            StringComparison.Ordinal);

    public static string LastCompletedPhase => Volatile.Read(ref _lastCompletedPhase);

    public static void Activate()
    {
        if (Enabled)
            Volatile.Write(ref _active, 1);
    }

    public static void Begin(string phase) => WriteOnce($"{phase}.begin");

    public static void Complete(string phase, string? detail = null)
    {
        if (!IsActive)
            return;
        Volatile.Write(ref _lastCompletedPhase, phase);
        WriteOnce($"{phase}.end", detail);
    }

    public static void WriteOnce(string phase, string? detail = null)
    {
        if (!IsActive || !LoggedPhases.TryAdd(phase, 0))
            return;

        string suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}";
        lock (WriteGate)
        {
            Console.Error.WriteLine(
                $"[shutdown] monotonicMs={Environment.TickCount64} " +
                $"managedTid={Environment.CurrentManagedThreadId} phase={phase}{suffix}");
        }
    }

    private static bool IsActive => Enabled && Volatile.Read(ref _active) != 0;
}

namespace ExtremeEditor.Wpf.Native;

/// <summary>Verbose native snapshot timings; opt in only when diagnosing editor performance.</summary>
internal static class NativePreparationLog
{
    internal const string EnvironmentVariable = "EXTREMEEDITOR_NATIVE_PREPARE_LOG";
    internal static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "1", StringComparison.Ordinal);
}

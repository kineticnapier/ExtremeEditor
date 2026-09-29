namespace ExtremeEditor.Wpf.Native;

internal static class DecorationDiagnostics
{
    internal const string EnvironmentVariable = "EXTREMEEDITOR_DECORATION_DIAGNOSTICS";

    internal static bool Enabled => string.Equals(
        Environment.GetEnvironmentVariable(EnvironmentVariable),
        "1",
        StringComparison.Ordinal);
}

internal static class NativeUploadDiagnostics
{
    internal const string EnvironmentVariable = "EXTREMEEDITOR_NATIVE_UPLOAD_DIAGNOSTICS";

    internal static bool Enabled => string.Equals(
        Environment.GetEnvironmentVariable(EnvironmentVariable),
        "1",
        StringComparison.Ordinal);
}

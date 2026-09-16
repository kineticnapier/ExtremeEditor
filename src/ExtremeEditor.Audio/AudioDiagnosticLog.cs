using System.Diagnostics;

namespace ExtremeEditor.Audio;

internal sealed class AudioDiagnosticLog
{
    public const string EnvironmentVariable = "EXTREMEEDITOR_DIAGNOSTICS";

    private readonly TextWriter _writer;
    private readonly object _gate = new();

    public static AudioDiagnosticLog? Shared { get; } = OpenFromEnvironment();

    public AudioDiagnosticLog(TextWriter writer) => _writer = writer;

    public IDisposable Measure(string phase, string details = "")
    {
        WriteLine($"BEGIN phase={phase}{FormatDetails(details)}");
        return new Measurement(this, phase, details);
    }

    public void Write(string eventName, string details = "") =>
        WriteLine($"event={eventName}{FormatDetails(details)}");

    public IDisposable StartHeartbeat(
        string eventName,
        TimeSpan interval,
        Func<string> detailsProvider)
    {
        return new Timer(_ =>
        {
            try
            {
                Write(eventName, detailsProvider());
            }
            catch
            {
                // Diagnostics must never disrupt the operation being measured.
            }
        }, null, interval, interval);
    }

    private void WriteLine(string message)
    {
        lock (_gate)
        {
            _writer.WriteLine(
                $"{DateTimeOffset.UtcNow:O} thread={Environment.CurrentManagedThreadId} {message}");
            _writer.Flush();
        }
    }

    private static string FormatDetails(string details) =>
        string.IsNullOrWhiteSpace(details) ? "" : $" {details}";

    private static AudioDiagnosticLog? OpenFromEnvironment()
    {
        string? configuredPath = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        try
        {
            string path = configuredPath == "1"
                ? Path.Combine(Path.GetTempPath(), "ExtremeEditor-first-load.log")
                : Path.GetFullPath(configuredPath);
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var writer = new StreamWriter(path, append: true) { AutoFlush = true };
            var log = new AudioDiagnosticLog(TextWriter.Synchronized(writer));
            log.Write("diagnostics.enabled", $"path={path} process={Environment.ProcessId}");
            return log;
        }
        catch
        {
            return null;
        }
    }

    private sealed class Measurement(AudioDiagnosticLog owner, string phase, string details) : IDisposable
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _watch.Stop();
            owner.WriteLine(
                $"END phase={phase} elapsed_ms={_watch.Elapsed.TotalMilliseconds:F3}{FormatDetails(details)}");
        }
    }
}

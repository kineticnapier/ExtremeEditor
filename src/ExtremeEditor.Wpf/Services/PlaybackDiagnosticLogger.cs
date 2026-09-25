using System.Collections.Concurrent;
using System.IO;

namespace ExtremeEditor.Wpf;

internal sealed class PlaybackDiagnosticLogger : IDisposable
{
    private readonly TextWriter _console;
    private readonly string _filePath;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
    private readonly Task _writerTask;
    private bool _disposed;

    internal PlaybackDiagnosticLogger(TextWriter console, string filePath)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        _console = console;
        _filePath = filePath;
        _writerTask = Task.Run(WriterLoop);
    }

    internal void Log(string line)
    {
        if (_disposed)
            return;

        string stamped = $"[{DateTimeOffset.Now:O}] {line}";
        try
        {
            _queue.Add(stamped);
        }
        catch (InvalidOperationException)
        {
            // Dispose may complete the queue concurrently with a final UI sample.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _queue.CompleteAdding();
        _writerTask.GetAwaiter().GetResult();
        _queue.Dispose();
    }

    private void WriterLoop()
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var file = new StreamWriter(_filePath, append: true)
        {
            AutoFlush = true
        };

        foreach (string line in _queue.GetConsumingEnumerable())
        {
            try
            {
                _console.WriteLine(line);
                _console.Flush();
                file.WriteLine(line);
            }
            catch
            {
                // Diagnostics must never destabilize playback.
            }
        }
    }
}

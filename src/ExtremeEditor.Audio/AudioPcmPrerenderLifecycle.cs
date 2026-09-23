namespace ExtremeEditor.Audio;

/// <summary>
/// Owns the cancellable progressive pre-render session for one hit-sound provider.
/// Prime work is synchronous so playback/seek can resume with the immediate range
/// already cached; speculative work continues on the provider's background task.
/// </summary>
internal sealed class AudioPcmPrerenderLifecycle : IDisposable
{
    private SampleAccurateHitSoundProvider? _provider;
    private int _sampleRate;
    private long _cacheBudgetBytes;
    private CancellationTokenSource? _cancellation;
    private Task? _backgroundTask;

    internal void Start(
        SampleAccurateHitSoundProvider provider,
        int sampleRate,
        long startFrame,
        long availableMemoryBytes)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Cancel();

        _provider = provider;
        _sampleRate = sampleRate;
        _cacheBudgetBytes = AudioPcmCachePolicy.CalculateAutoBudgetBytes(availableMemoryBytes);
        _provider.SetCacheBudgetBytes(_cacheBudgetBytes);
        StartCore(startFrame);
    }

    internal void Restart(long startFrame)
    {
        if (_provider is null)
            return;

        CancelBackgroundOnly();
        StartCore(startFrame);
    }

    internal void Cancel()
    {
        CancelBackgroundOnly();
        _provider = null;
        _sampleRate = 0;
        _cacheBudgetBytes = 0;
    }

    public void Dispose()
    {
        Cancel();
        GC.SuppressFinalize(this);
    }

    private void StartCore(long startFrame)
    {
        SampleAccurateHitSoundProvider provider = _provider!;
        long primeFrames = AudioPcmCachePolicy.CalculateInitialPrimeFrames(_sampleRate);
        provider.PrimeRange(startFrame, primeFrames);

        if (_cacheBudgetBytes <= 0)
            return;

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;

        long backgroundStart;
        try
        {
            backgroundStart = checked(startFrame + primeFrames);
        }
        catch (OverflowException)
        {
            backgroundStart = long.MaxValue;
        }

        _backgroundTask = provider.PrerenderAheadAsync(
            backgroundStart,
            _cacheBudgetBytes,
            cancellation.Token);
    }

    private void CancelBackgroundOnly()
    {
        CancellationTokenSource? cancellation = _cancellation;
        Task? backgroundTask = _backgroundTask;
        _cancellation = null;
        _backgroundTask = null;

        if (cancellation is null)
            return;

        try
        {
            cancellation.Cancel();
        }
        finally
        {
            cancellation.Dispose();
        }

        // Observe a cancellation/fault if the task completes later. Do not block
        // the playback/UI thread waiting for speculative rendering to stop.
        if (backgroundTask is not null)
        {
            _ = backgroundTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}

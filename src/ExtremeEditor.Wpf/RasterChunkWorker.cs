using System.Collections.Concurrent;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ExtremeEditor.Wpf;

internal sealed class RasterChunkWorker : IDisposable
{
    private readonly ConcurrentQueue<RasterChunkRequest>[] _pending =
    [
        new ConcurrentQueue<RasterChunkRequest>(),
        new ConcurrentQueue<RasterChunkRequest>(),
        new ConcurrentQueue<RasterChunkRequest>()
    ];
    private readonly ConcurrentQueue<RasterChunkResult> _completed = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private volatile bool _stopping;
    private int _requestedCount;
    private int _completedCount;
    private int _lastBuildThreadId;

    public RasterChunkWorker()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ExtremeEditor Raster Chunk Worker"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public int RequestedCount => Volatile.Read(ref _requestedCount);
    public int CompletedCount => Volatile.Read(ref _completedCount);
    public int LastBuildThreadId => Volatile.Read(ref _lastBuildThreadId);

    public void Enqueue(RasterChunkRequest request)
    {
        if (_stopping)
            return;

        int priority = Math.Clamp(request.Priority, 0, _pending.Length - 1);
        _pending[priority].Enqueue(request);
        Interlocked.Increment(ref _requestedCount);
        _signal.Set();
    }

    public bool TryDequeueCompleted(out RasterChunkResult? result)
    {
        if (_completed.TryDequeue(out RasterChunkResult? completed))
        {
            result = completed;
            return true;
        }

        result = null;
        return false;
    }

    public void Dispose()
    {
        if (_stopping)
            return;

        _stopping = true;
        _signal.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _signal.Dispose();
    }

    private void Run()
    {
        var renderer = new WpfFloorRenderer();

        while (true)
        {
            while (TryDequeueNext(out RasterChunkRequest? request))
            {
                if (_stopping)
                    return;

                try
                {
                    RasterChunkResult result = Build(renderer, request);
                    _completed.Enqueue(result);
                    Interlocked.Increment(ref _completedCount);
                }
                catch
                {
                    // A single bad chunk must not kill the worker thread.
                }
            }

            if (_stopping)
                return;

            _signal.WaitOne();
        }
    }

    private bool TryDequeueNext(out RasterChunkRequest? request)
    {
        foreach (ConcurrentQueue<RasterChunkRequest> queue in _pending)
        {
            if (queue.TryDequeue(out RasterChunkRequest? next))
            {
                request = next;
                return true;
            }
        }

        request = null;
        return false;
    }

    private RasterChunkResult Build(WpfFloorRenderer renderer, RasterChunkRequest request)
    {
        Volatile.Write(ref _lastBuildThreadId, Environment.CurrentManagedThreadId);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            renderer.BeginFrame(request.Zoom);
            foreach (int floor in request.CandidateFloorsDescending)
            {
                if ((uint)floor >= (uint)request.Positions.Length)
                    continue;

                Vector2 position = request.Positions[floor];
                if (!request.WorldRect.Contains(new Point(position.X, position.Y)))
                    continue;

                Point center = WorldToChunkPixel(position, request);
                GetFloorAngles(floor, request.Positions, out float entryAngle, out float exitAngle);
                bool midSpin = floor < request.Angles.Length && Math.Abs(request.Angles[floor] - 999.0) < 0.000001;
                renderer.DrawFloor(dc, center, request.Zoom, entryAngle, exitAngle, midSpin, selected: false);
            }
        }

        var bitmap = new RenderTargetBitmap(
            Math.Max(1, request.PixelWidth),
            Math.Max(1, request.PixelHeight),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return new RasterChunkResult(request.Key, request.ScreenRect, bitmap);
    }

    private static Point WorldToChunkPixel(Vector2 point, RasterChunkRequest request)
    {
        double x = (point.X - request.WorldRect.Left) * request.Zoom;
        double y = (request.WorldRect.Bottom - point.Y) * request.Zoom;
        return new Point(x, y);
    }

    private static void GetFloorAngles(int floor, Vector2[] positions, out float entryAngle, out float exitAngle)
    {
        Vector2 incoming = Vector2.Zero;
        Vector2 outgoing = Vector2.Zero;

        if (floor > 0)
            incoming = positions[floor - 1] - positions[floor];
        if (floor + 1 < positions.Length)
            outgoing = positions[floor + 1] - positions[floor];

        if (incoming.LengthSquared() < 0.000001f && outgoing.LengthSquared() >= 0.000001f)
            incoming = -outgoing;
        if (outgoing.LengthSquared() < 0.000001f && incoming.LengthSquared() >= 0.000001f)
            outgoing = -incoming;

        entryAngle = incoming.LengthSquared() < 0.000001f
            ? MathF.PI
            : MathF.Atan2(incoming.Y, incoming.X);
        exitAngle = outgoing.LengthSquared() < 0.000001f
            ? 0f
            : MathF.Atan2(outgoing.Y, outgoing.X);
    }
}

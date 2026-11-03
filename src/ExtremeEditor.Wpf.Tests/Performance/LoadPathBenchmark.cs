using System.Diagnostics;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

// Opt-in measurement of the chart load path without a window. It runs the
// same managed stages as MainWindow.LoadLevelAsync, one after another, and
// reports elapsed time and allocated bytes per stage. It asserts nothing.
//
//   EXTREMEEDITOR_LOAD_BENCH=<chart.adofai>  EXTREMEEDITOR_LOAD_BENCH_RUNS=3
internal static class LoadPathBenchmark
{
    internal const string PathVariable = "EXTREMEEDITOR_LOAD_BENCH";

    public static void Run(string path)
    {
        int runs = int.TryParse(Environment.GetEnvironmentVariable("EXTREMEEDITOR_LOAD_BENCH_RUNS"), out int n)
            ? Math.Clamp(n, 1, 20)
            : 1;
        Console.WriteLine($"ExtremeEditor {EditorVersion.Current} load bench: {path} runs={runs}");
        var samples = new Dictionary<string, List<(double Ms, double AllocMiB)>>(StringComparer.Ordinal);
        var order = new List<string>();

        for (int run = 0; run < runs; run++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var stages = new List<(string Name, double Ms, double AllocMiB)>();

            T Stage<T>(string name, Func<T> body)
            {
                long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
                var watch = Stopwatch.StartNew();
                T result = body();
                watch.Stop();
                long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;
                stages.Add((name, watch.Elapsed.TotalMilliseconds, allocated / 1024d / 1024d));
                return result;
            }

            bool breakdown = Environment.GetEnvironmentVariable("EXTREMEEDITOR_LOAD_BENCH_BREAKDOWN") == "1";
            if (breakdown && run == 0)
            {
                Stage("pass.core (AdoFaiLoader)", () => AdoFaiLoader.LoadFlatAsync(path).GetAwaiter().GetResult());
                Stage("pass.trackVisual", () => TrackVisualSourceReader.Load(path, default));
                Stage("pass.trackTransform", () => TrackTransformSourceReader.Load(path, default));
                Stage("pass.scaleRadius", () => ScaleRadiusSourceReader.Load(path, default));
                Stage("pass.camera", () => CameraSourceReader.Load(path, default));
                GC.Collect();
            }

            var total = Stopwatch.StartNew();
            WpfLevelLoadResult loaded = Stage("load (parse+path+index)", () => WpfLevelLoader.Load(path));
            LevelDocument level = loaded.Document;
            TimingMap timing = Stage("timingMap", () => FlatTimingMapBuilder.Build(level));
            Stage("hitSoundTimeline", () => HitSoundTimelineBuilder.Build(level));
            PreparedNativeLevel prepared = Stage("prepareLevel (native)", () => NativeLevelViewport.PrepareLevel(level));
            double editorReady = total.Elapsed.TotalMilliseconds;
            PreparedNativePlayback playback = Stage(
                "preparePlayback (native)", () => NativeLevelViewport.PreparePlayback(level, timing));
            double playbackManaged = total.Elapsed.TotalMilliseconds;
            NativeHeadlessRendererSession session = Stage(
                "native upload (level+timelines)",
                () => NativeHeadlessRendererSession.CreateFromPrepared(() => prepared, () => playback, 64, 64));
            total.Stop();
            using (Process self = Process.GetCurrentProcess())
            {
                Console.WriteLine(
                    $"run {run + 1}: afterUpload private={self.PrivateMemorySize64 / 1048576.0:F0}MiB " +
                    $"managedHeap={GC.GetTotalMemory(false) / 1048576.0:F0}MiB " +
                    $"floors={prepared.Snapshot.Floors.Length:N0} " +
                    $"floorBytes={(long)prepared.Snapshot.Floors.Length * System.Runtime.InteropServices.Marshal.SizeOf<NativeFloor>() / 1048576.0:F0}MiB");
            }
            session.Dispose();
            NativeLevelSnapshot snapshot = prepared.Snapshot;

            if (breakdown)
            {
                // These repeat work already counted above.
                Stage("  snapshot (flat builder)", () => FlatNativeLevelSnapshotBuilder.BuildProfiled(level).Snapshot);
                Stage("  backgroundInitial", () => BackgroundSourceReader.ReadInitial(level));
                Stage("  tl.playback", () => NativePlaybackTimelineBuilder.Build(timing));
                Stage("  tl.camera", () => FaithfulNativeCameraTimelineBuilder.Build(level, timing));
                Stage("  tl.background", () => BackgroundTimelineBuilder.Build(level, timing));
                StaticTrackTransform[] statics = Stage("  tl.trackTransformStatic", () => TrackTransformResolver.ResolveStatic(level));
                Stage("  tl.trackTransformMove", () => TrackTransformResolver.BuildMoveTimeline(level, timing, statics));
                Stage("  tl.trackVisual", () => TrackVisualTimelineBuilder.Build(level, timing));
                Stage("  tl.trackAnimation", () => TrackAnimationTimelineBuilder.Build(level, timing));
            }

            GC.KeepAlive(snapshot);
            foreach ((string name, double ms, double alloc) in stages)
            {
                if (!samples.TryGetValue(name, out var list))
                {
                    samples[name] = list = [];
                    order.Add(name);
                }
                list.Add((ms, alloc));
            }
            Add("= editor-ready managed", editorReady, 0);
            Add("= playback managed", playbackManaged, 0);
            Add("= through native upload", total.Elapsed.TotalMilliseconds, 0);
            Console.WriteLine(
                $"run {run + 1}: editorReadyManaged={editorReady:F0}ms playbackManaged={playbackManaged:F0}ms " +
                $"heap={GC.GetGCMemoryInfo().HeapSizeBytes / 1024d / 1024d:F0}MiB " +
                $"peakWorkingSet={Process.GetCurrentProcess().PeakWorkingSet64 / 1024d / 1024d:F0}MiB");

            void Add(string name, double ms, double alloc)
            {
                if (!samples.TryGetValue(name, out var list))
                {
                    samples[name] = list = [];
                    order.Add(name);
                }
                list.Add((ms, alloc));
            }
        }

        Console.WriteLine($"{"stage",-28} {"median ms",10} {"min ms",10} {"alloc MiB",10}");
        foreach (string name in order)
        {
            List<(double Ms, double AllocMiB)> list = samples[name];
            double[] ms = list.Select(s => s.Ms).Order().ToArray();
            double median = ms[ms.Length / 2];
            double alloc = list.Select(s => s.AllocMiB).Order().ToArray()[list.Count / 2];
            Console.WriteLine($"{name,-28} {median,10:F1} {ms[0],10:F1} {alloc,10:F1}");
        }
        Console.WriteLine(
            $"process peakWorkingSet={Process.GetCurrentProcess().PeakWorkingSet64 / 1024d / 1024d:F0}MiB");
    }
}

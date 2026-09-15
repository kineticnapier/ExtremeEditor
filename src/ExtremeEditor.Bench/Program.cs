using System.Diagnostics;
using ExtremeEditor.Core;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: ExtremeEditor.Bench <level.adofai>");
    return 2;
}

string path = Path.GetFullPath(args[0]);
Console.WriteLine($"ExtremeEditor {EditorVersion.Current}");
Console.WriteLine(path);

var total = Stopwatch.StartNew();
LoadResult loaded = AdoFaiLoader.Load(path);

var sw = Stopwatch.StartNew();
var index = new SpatialGridIndex(loaded.Document.Positions);
sw.Stop();
TimeSpan indexTime = sw.Elapsed;

var queryBuffer = new List<int>(8192);
WorldRect bounds = loaded.Document.Bounds;
float cx = (bounds.Left + bounds.Right) * .5f;
float cy = (bounds.Top + bounds.Bottom) * .5f;
var viewport = new WorldRect(cx - 20, cy - 12, cx + 20, cy + 12);

const int queryIterations = 10_000;
sw.Restart();
long candidates = 0;
for (int i = 0; i < queryIterations; i++)
{
    index.Query(viewport, queryBuffer);
    candidates += queryBuffer.Count;
}
sw.Stop();
total.Stop();

Console.WriteLine($"file        : {loaded.Metrics.FileBytes / 1024d / 1024d:F2} MiB");
Console.WriteLine($"floors      : {loaded.Document.FloorCount:N0}");
Console.WriteLine($"actions     : {loaded.Document.ActionCount:N0}");
foreach (var pair in loaded.Document.ActionTypeCounts.OrderByDescending(x => x.Value).Take(8))
    Console.WriteLine($"  {pair.Key,-20} {pair.Value,10:N0}");
Console.WriteLine($"read        : {loaded.Metrics.Read.TotalMilliseconds,10:F3} ms");
Console.WriteLine($"parse       : {loaded.Metrics.Parse.TotalMilliseconds,10:F3} ms");
Console.WriteLine($"path        : {loaded.Metrics.BuildPath.TotalMilliseconds,10:F3} ms");
Console.WriteLine($"spatial idx : {indexTime.TotalMilliseconds,10:F3} ms ({index.CellCount:N0} cells)");
Console.WriteLine($"query x{queryIterations:N0}: {sw.Elapsed.TotalMilliseconds,10:F3} ms");
Console.WriteLine($"query avg   : {sw.Elapsed.TotalMilliseconds / queryIterations,10:F6} ms");
Console.WriteLine($"candidates  : {(double)candidates / queryIterations:N1} avg");
Console.WriteLine($"total       : {total.Elapsed.TotalMilliseconds,10:F3} ms");
return 0;

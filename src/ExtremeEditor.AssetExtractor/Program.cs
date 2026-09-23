namespace ExtremeEditor.AssetExtractor;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length < 1 || args.Length > 2)
            {
                Console.Error.WriteLine("Usage: ExtremeEditor.AssetExtractor <adofai-root> [output-directory]");
                return 2;
            }

            string gameRoot = Path.GetFullPath(args[0].Trim('"'));
            string outputDirectory = args.Length >= 2
                ? Path.GetFullPath(args[1].Trim('"'))
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ExtremeEditor",
                    "AssetCache",
                    "floor-mesh");

            FloorTextureExtractionResult result = AdoFaiFloorTextureExtractor.Extract(gameRoot, outputDirectory);
            Console.WriteLine($"[extract] output={result.OutputDirectory}");
            Console.WriteLine($"[extract] tile={result.TilePath}");
            Console.WriteLine($"[extract] perlin={result.PerlinPath}");
            Console.WriteLine($"[extract] ramp={result.RampPath}");
            Console.WriteLine($"[extract] glow={result.GlowPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"extract fatal: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}

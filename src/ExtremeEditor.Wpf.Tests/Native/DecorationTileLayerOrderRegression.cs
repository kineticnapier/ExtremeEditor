using System.IO;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Export;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

// Stock scrVisualDecoration.SetDepth puts depth >= 0 on sorting layer "Bg" and
// depth < 0 on "Default". ADOFAI's TagManager orders the sorting layers
// Bg3, Bg2(Lights), Bg, Floor, FloorCover, FloorTop, Ring, Planet, Default, ...
// and the main Camera renders object layers 7 (Foreground), 8 (Planet) and
// 9 (Floor), so background decorations sit behind tiles and foreground
// decorations cover tiles and planets.
internal static class DecorationTileLayerOrderRegression
{
    private const int Width = 160;
    private const int Height = 90;

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RunIsolatedWhenRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_DECORATION_LAYER_ORDER_ONLY"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Run();
            Console.WriteLine("PASS: decoration/tile layer order follows stock sorting layers.");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex}");
            Environment.Exit(1);
        }
    }

    public static void Run()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), $"ExtremeEditor-DecorationLayer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            WriteSolidBitmap(Path.Combine(directory, "solid.bmp"));
            RenderedFrame control = RenderFixture(directory, "control", []);
            int[] tile = InteriorTilePixels(control);
            if (tile.Length < 16)
                throw new InvalidOperationException($"Fixture shows too few interior tile pixels: {tile.Length}.");
            int corner = 0;
            if (IsTile(control, corner))
                throw new InvalidOperationException("Fixture corner pixel is covered by a tile.");

            // A. Depth boundary: 0 and 1 are background, -1 is foreground.
            foreach (int depth in new[] { 0, 1, 50 })
            {
                RenderedFrame frame = RenderFixture(directory, $"bg{depth}", [Deco("ff0000", depth)]);
                RequireColor(frame, corner, 255, 0, 0, $"depth {depth} background decoration is not drawn");
                RequireTileUnchanged(control, frame, tile, $"depth {depth} decoration covered the tiles");
            }

            foreach (int depth in new[] { -1, -50 })
            {
                RenderedFrame frame = RenderFixture(directory, $"fg{-depth}", [Deco("ff0000", depth)]);
                RequireColor(frame, corner, 255, 0, 0, $"depth {depth} foreground decoration is not drawn");
                foreach (int pixel in tile)
                    RequireColor(frame, pixel, 255, 0, 0, $"depth {depth} decoration did not cover the tiles");
            }

            // Order inside one sorting layer stays sortingOrder = -depth.
            RenderedFrame bgOrder = RenderFixture(directory, "bg-order",
                [Deco("00ff00", 5), Deco("ff0000", 1)]);
            RequireColor(bgOrder, corner, 255, 0, 0, "background depth 1 is not above depth 5");
            RequireTileUnchanged(control, bgOrder, tile, "background pair covered the tiles");

            RenderedFrame fgOrder = RenderFixture(directory, "fg-order",
                [Deco("ff0000", -1), Deco("00ff00", -5)]);
            RequireColor(fgOrder, corner, 0, 255, 0, "foreground depth -5 is not above depth -1");
            foreach (int pixel in tile)
                RequireColor(fgOrder, pixel, 0, 255, 0, "foreground pair did not cover the tiles");

            // Mixed: a foreground decoration covers both the tiles and the
            // background decoration; the background one stays behind the tiles.
            RenderedFrame mixed = RenderFixture(directory, "mixed",
                [Deco("ff0000", 10), Deco("0000ff", -10, scaleX: 2000, scaleY: 6000, positionX: -5)]);
            RequireTileUnchanged(control, mixed, InsideRight(tile), "background half covered the tiles");
            // The blue strip is centred at x = Width / 4 and spans the full height.
            RequireColor(mixed, Width / 4, 0, 0, 255, "foreground strip is not above the background decoration");
            RequireColor(mixed, Width - 1, 255, 0, 0, "background decoration is missing beside the strip");

            // Blend modes sample only what is beneath their own layer.
            RenderedFrame bgDifference = RenderFixture(directory, "bg-difference",
                [Deco("ffffff", 1, blend: "Difference")]);
            RequireTileUnchanged(control, bgDifference, tile, "background Difference covered the tiles");
            RequireInverted(control, bgDifference, corner, "background Difference did not invert the background");

            RenderedFrame fgDifference = RenderFixture(directory, "fg-difference",
                [Deco("ffffff", -1, blend: "Difference")]);
            foreach (int pixel in tile)
                RequireInverted(control, fgDifference, pixel, "foreground Difference did not invert the tiles");

            RenderedFrame bgDodge = RenderFixture(directory, "bg-dodge",
                [Deco("404040", 1, blend: "LinearDodge")]);
            RequireTileUnchanged(control, bgDodge, tile, "background LinearDodge covered the tiles");

            // A full-screen mask still reveals a background VisibleInsideMask target
            // behind the tiles, and a foreground one above them.
            RenderedFrame bgMasked = RenderFixture(directory, "bg-masked",
                [MaskDeco(), Deco("ff0000", 1, masking: "VisibleInsideMask")]);
            RequireColor(bgMasked, corner, 255, 0, 0, "masked background decoration is not drawn");
            RequireTileUnchanged(control, bgMasked, tile, "masked background decoration covered the tiles");

            RenderedFrame fgMasked = RenderFixture(directory, "fg-masked",
                [MaskDeco(), Deco("ff0000", -1, masking: "VisibleInsideMask")]);
            foreach (int pixel in tile)
                RequireColor(fgMasked, pixel, 255, 0, 0, "masked foreground decoration did not cover the tiles");

            // Semi-transparent background RGBA still stays behind the tiles.
            RenderedFrame bgAlpha = RenderFixture(directory, "bg-alpha", [Deco("ff000080", 1)]);
            RequireTileUnchanged(control, bgAlpha, tile, "semi-transparent background covered the tiles");

            VerifyVideoMatchesHeadless(directory);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Best effort: test correctness does not depend on temp cleanup.
            }
        }
    }

    // Video export renders through RenderBorrowed; its frames must equal the
    // headless Render output for the same layered chart and time.
    private static void VerifyVideoMatchesHeadless(string directory)
    {
        string path = WriteFixture(directory, "video",
            [Deco("ff0000", 3), Deco("0000ff", -3, scaleX: 2000, scaleY: 6000, positionX: -5)]);
        using var headless = new NativeChartFrameRenderer(Width, Height);
        headless.Load(path);
        RenderedFrame expected = headless.Render(0);

        using var renderer = new NativeChartFrameRenderer(Width, Height);
        renderer.Load(path);
        var pipeline = new VideoExportFramePipeline(renderer);
        var plan = new VideoExportPlan(0, 0.05, 20, Width, Height);
        byte[]? first = null;
        pipeline.RenderAsync(
                plan,
                (_, rendered, _) =>
                {
                    first ??= rendered.Rgb.ToArray();
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        if (first is null || !expected.Rgb.AsSpan().SequenceEqual(first))
            throw new InvalidOperationException("Video frame layer order differs from headless rendering.");
    }

    private static string Deco(
        string color,
        int depth,
        string blend = "None",
        string masking = "None",
        double scaleX = 8000,
        double scaleY = 6000,
        double positionX = 0) =>
        "{ \"floor\": 0, \"eventType\": \"AddDecoration\", \"decorationImage\": \"solid.bmp\", " +
        "\"relativeTo\": \"Camera\", " +
        $"\"position\": [{positionX.ToString(System.Globalization.CultureInfo.InvariantCulture)}, 0], " +
        $"\"scale\": [{scaleX.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
        $"{scaleY.ToString(System.Globalization.CultureInfo.InvariantCulture)}], " +
        $"\"color\": \"{color}\", \"opacity\": 100, \"visible\": true, \"depth\": {depth}, " +
        $"\"blendMode\": \"{blend}\", \"maskingType\": \"{masking}\", \"lockScale\": true }}";

    private static string MaskDeco() =>
        "{ \"floor\": 0, \"eventType\": \"AddDecoration\", \"decorationImage\": \"solid.bmp\", " +
        "\"relativeTo\": \"Camera\", \"position\": [0, 0], \"scale\": [8000, 6000], \"opacity\": 100, " +
        "\"visible\": true, \"maskingType\": \"Mask\", \"depth\": -25, \"lockScale\": true }";

    private static RenderedFrame RenderFixture(string directory, string name, string[] decorations)
    {
        using var renderer = new NativeChartFrameRenderer(Width, Height);
        renderer.Load(WriteFixture(directory, name, decorations));
        return renderer.Render(0);
    }

    private static string WriteFixture(string directory, string name, string[] decorations)
    {
        string path = Path.Combine(directory, name + ".adofai");
        File.WriteAllText(path,
            "{\"angleData\":[0,0,0,0,0,0],\"settings\":{\"bpm\":60,\"zoom\":100}," +
            $"\"decorations\":[{string.Join(",", decorations)}]}}");
        return path;
    }

    private static int[] InteriorTilePixels(RenderedFrame control)
    {
        var pixels = new List<int>();
        for (int y = 1; y < Height - 1; y++)
        {
            for (int x = 1; x < Width - 1; x++)
            {
                int index = y * Width + x;
                if (IsTile(control, index) && IsTile(control, index - 1) && IsTile(control, index + 1) &&
                    IsTile(control, index - Width) && IsTile(control, index + Width))
                {
                    pixels.Add(index);
                }
            }
        }
        return pixels.ToArray();
    }

    private static int[] InsideRight(int[] tile) =>
        tile.Where(index => index % Width > Width / 2 + 4).ToArray();

    // The left edge of the frame holds only the background colour.
    private static bool IsTile(RenderedFrame frame, int pixel)
    {
        int background = 0;
        int difference = 0;
        for (int channel = 0; channel < 3; channel++)
            difference += Math.Abs(frame.Rgb[pixel * 3 + channel] - frame.Rgb[background + channel]);
        return difference > 60;
    }

    private static void RequireTileUnchanged(
        RenderedFrame control,
        RenderedFrame frame,
        int[] tile,
        string message)
    {
        if (tile.Length == 0)
            throw new InvalidOperationException($"{message}: no tile pixels to compare.");
        int changed = 0;
        foreach (int pixel in tile)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                if (Math.Abs(control.Rgb[pixel * 3 + channel] - frame.Rgb[pixel * 3 + channel]) > 2)
                {
                    changed++;
                    break;
                }
            }
        }
        if (changed > 0)
            throw new InvalidOperationException($"{message}: {changed}/{tile.Length} tile pixels changed.");
    }

    private static void RequireColor(RenderedFrame frame, int pixel, int red, int green, int blue, string message)
    {
        int offset = pixel * 3;
        if (Math.Abs(frame.Rgb[offset] - red) > 2 ||
            Math.Abs(frame.Rgb[offset + 1] - green) > 2 ||
            Math.Abs(frame.Rgb[offset + 2] - blue) > 2)
        {
            throw new InvalidOperationException(
                $"{message}: pixel ({pixel % Width},{pixel / Width}) is " +
                $"({frame.Rgb[offset]},{frame.Rgb[offset + 1]},{frame.Rgb[offset + 2]}).");
        }
    }

    private static void RequireInverted(RenderedFrame control, RenderedFrame frame, int pixel, string message)
    {
        int offset = pixel * 3;
        for (int channel = 0; channel < 3; channel++)
        {
            int expected = 255 - control.Rgb[offset + channel];
            if (Math.Abs(frame.Rgb[offset + channel] - expected) > 3)
            {
                throw new InvalidOperationException(
                    $"{message}: pixel ({pixel % Width},{pixel / Width}) channel {channel} " +
                    $"is {frame.Rgb[offset + channel]}, expected {expected}.");
            }
        }
    }

    private static void WriteSolidBitmap(string path)
    {
        const int size = 32;
        const int pixelBytes = size * size * 4;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4d42);
        writer.Write(14 + 40 + pixelBytes);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(14 + 40);
        writer.Write(40);
        writer.Write(size);
        writer.Write(size);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(pixelBytes);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);
        for (int index = 0; index < size * size; index++)
            writer.Write(0xffffffffu);
    }
}

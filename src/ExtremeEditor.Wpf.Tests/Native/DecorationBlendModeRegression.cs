using System.IO;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class DecorationBlendModeRegression
{
    private const uint BlendModeBits = 0x7000u;
    private const uint MaskingTypeBits = 0x300u;
    private const uint MaskingVisibleInside = 0x200u;

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RunIsolatedWhenRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_DECORATION_BLEND_ONLY"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Run();
            Console.WriteLine("PASS: decoration blend-mode packing regression is valid.");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex.Message}");
            Environment.Exit(1);
        }
    }

    public static void Run()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-DecorationBlend-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            string imagePath = Path.Combine(directory, "pixel.png");
            WriteOnePixelPng(imagePath);
            string levelPath = Path.Combine(directory, "blend-modes.adofai");
            File.WriteAllText(levelPath, """
            {
              "angleData": [0, 90],
              "settings": { "bpm": 100 },
              "actions": [],
              "decorations": [
                { "floor": 0, "eventType": "AddDecoration", "decorationImage": "pixel.png", "position": [0,0], "scale": [100,100], "opacity": 100, "visible": true, "relativeTo": "Global", "blendMode": "None" },
                { "floor": 0, "eventType": "AddDecoration", "decorationImage": "pixel.png", "position": [0,0], "scale": [100,100], "opacity": 100, "visible": true, "relativeTo": "Global", "blendMode": "LinearDodge" },
                { "floor": 0, "eventType": "AddDecoration", "decorationImage": "pixel.png", "position": [0,0], "scale": [100,100], "opacity": 100, "visible": true, "relativeTo": "Global", "blendMode": "Multiply" },
                { "floor": 0, "eventType": "AddDecoration", "decorationImage": "pixel.png", "position": [0,0], "scale": [100,100], "opacity": 100, "visible": true, "relativeTo": "Global", "blendMode": "Screen" },
                { "floor": 0, "eventType": "AddDecoration", "decorationImage": "pixel.png", "position": [0,0], "scale": [100,100], "opacity": 100, "visible": true, "relativeTo": "Global", "blendMode": "Overlay", "maskingType": "VisibleInsideMask" },
                { "floor": 0, "eventType": "AddDecoration", "decorationImage": "pixel.png", "position": [0,0], "scale": [100,100], "opacity": 100, "visible": true, "relativeTo": "Global", "blendMode": "SoftLight" },
                { "floor": 0, "eventType": "AddDecoration", "decorationImage": "pixel.png", "position": [0,0], "scale": [100,100], "opacity": 100, "visible": true, "relativeTo": "Global", "blendMode": "Difference" }
              ]
            }
            """);

            LevelDocument level = AdoFaiLoader.Load(levelPath).Document;
            StaticDecorationSnapshotData data = StaticDecorationSnapshotBuilder.Build(level);
            if (data.Instances.Length != 7)
                throw new InvalidOperationException("Blend fixture did not produce seven native decorations.");

            uint[] expected =
            [
                0x0000u,
                0x1000u,
                0x2000u,
                0x3000u,
                0x4000u,
                0x5000u,
                0x6000u
            ];

            foreach (NativeStaticDecoration instance in data.Instances)
            {
                if ((uint)instance.SourceIndex >= (uint)expected.Length)
                    throw new InvalidOperationException("Blend fixture produced an unexpected source index.");

                uint actual = instance.Flags & BlendModeBits;
                uint wanted = expected[instance.SourceIndex];
                if (actual != wanted)
                {
                    throw new InvalidOperationException(
                        $"Blend mode source {instance.SourceIndex} packed 0x{actual:X4}; expected 0x{wanted:X4}.");
                }
            }

            NativeStaticDecoration overlay = data.Instances.Single(instance => instance.SourceIndex == 4);
            if ((overlay.Flags & MaskingTypeBits) != MaskingVisibleInside ||
                (overlay.Flags & BlendModeBits) != 0x4000u)
            {
                throw new InvalidOperationException(
                    "Masking and Overlay blend flags did not coexist in the 96-byte decoration ABI.");
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteOnePixelPng(string path)
    {
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAFgwJ/l1uHOwAAAABJRU5ErkJggg==");
        File.WriteAllBytes(path, png);
    }
}

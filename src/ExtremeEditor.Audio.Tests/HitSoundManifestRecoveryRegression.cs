using System.Reflection;
using System.Runtime.CompilerServices;
using ExtremeEditor.Audio;

internal static class HitSoundManifestRecoveryRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        string manifest = Path.Combine(
            Path.GetTempPath(), $"extremeeditor-hitsounds-{Guid.NewGuid():N}.tsv");
        try
        {
            File.WriteAllText(manifest,
                "name\tfile\toffset\n" +
                "sndKick\tkick.wav\t0.125\n");

            var library = new HitSoundLibrary();
            using var output = new StringWriter();
            var log = new AudioDiagnosticLog(output);

            // A different process can temporarily hold the manifest. Its
            // failure must not prevent the rest of the hit-sound library from
            // loading or permanently poison its lazy initialization.
            using (var exclusive = new FileStream(
                       manifest, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                library.LoadManifestSafely(manifest, log);
            }

            if (!output.ToString().Contains(
                    "event=hitsounds.manifest_unavailable", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A locked hit-sound manifest was not reported as unavailable.");
            }

            // The manifest must still be readable after the other process
            // releases its lock; recovery must not disable normal offsets.
            library.LoadManifestSafely(manifest, log);
            FieldInfo offsetsField = typeof(HitSoundLibrary).GetField(
                "_offsets", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Hit-sound offsets field missing.");
            var offsets = (Dictionary<string, double>?)offsetsField.GetValue(library);
            if (offsets is null ||
                !offsets.TryGetValue("Kick", out double offset) ||
                Math.Abs(offset - 0.125) > 1e-12)
            {
                throw new InvalidOperationException(
                    "Manifest offsets did not load after releasing the file lock.");
            }
        }
        finally
        {
            File.Delete(manifest);
        }
    }
}

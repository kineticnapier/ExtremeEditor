using ExtremeEditor.Audio;
using NAudio.Wave;

internal static class MultichannelFoldDownRegression
{
    internal static void Run()
    {
        const int sampleRate = 48_000;

        // Preserve existing stereo inputs without wrapping or modifying them.
        var stereo = new FixedSamples(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
            [0.25f, -0.5f]);
        if (!ReferenceEquals(stereo, AudioGraphFactory.NormalizeSong(stereo)))
            throw new InvalidOperationException("Stereo input should remain unchanged.");

        // A third-channel-only frame must remain audible after conversion.
        var three = AudioGraphFactory.NormalizeSong(new FixedSamples(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 3),
            [0f, 0f, 1f, 0.25f, -0.25f, 0f]));
        if (three.WaveFormat.Channels != 2 || three.WaveFormat.SampleRate != sampleRate)
            throw new InvalidOperationException("3ch format was not converted to 48 kHz stereo.");

        float[] threeOut = new float[6];
        int read = three.Read(threeOut, 1, 4);
        if (read != 4 || threeOut[0] != 0f || threeOut[5] != 0f)
            throw new InvalidOperationException("3ch output count/offset contract failed.");
        Near(0.70710678f, threeOut[1], "third channel contributes to left");
        Near(0.70710678f, threeOut[2], "third channel contributes to right");
        Near(0.25f, threeOut[3], "front left remains unchanged");
        Near(-0.25f, threeOut[4], "front right remains unchanged");
        if (three.Read(threeOut, 0, 4) != 0)
            throw new InvalidOperationException("3ch source must reach end-of-stream.");

        // A signal carried only in the sixth channel must not be discarded.
        var six = AudioGraphFactory.NormalizeSong(new FixedSamples(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 6),
            [0f, 0f, 0f, 0f, 0f, 1f]));
        float[] sixOut = new float[2];
        if (six.Read(sixOut, 0, 2) != 2)
            throw new InvalidOperationException("6ch frame count changed.");
        Near(0.70710678f / 4f, sixOut[0], "sixth channel contributes to left");
        Near(0.70710678f / 4f, sixOut[1], "sixth channel contributes to right");

        // Short decoder reads may split a 3-channel frame. The partial
        // samples must survive into the next call without shifting channels.
        var chunkedThree = AudioGraphFactory.NormalizeSong(new ChunkedSamples(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 3),
            [0f, 0f, 1f, 0.25f, -0.25f, 0f], 2));
        float[] chunkedOut = new float[6];
        if (chunkedThree.Read(chunkedOut, 0, 0) != 0)
            throw new InvalidOperationException("A zero-length request consumed input.");
        if (chunkedThree.Read(chunkedOut, 1, 2) != 2 ||
            chunkedThree.Read(chunkedOut, 3, 2) != 2)
            throw new InvalidOperationException("Chunked 3ch frames were lost.");
        Near(0.70710678f, chunkedOut[1], "chunked 3ch first left");
        Near(0.70710678f, chunkedOut[2], "chunked 3ch first right");
        Near(0.25f, chunkedOut[3], "chunked 3ch second left");
        Near(-0.25f, chunkedOut[4], "chunked 3ch second right");
        if (chunkedThree.Read(chunkedOut, 1, 2) != 0)
            throw new InvalidOperationException("Chunked 3ch source must end.");

        // Odd-sized stereo requests are legal. In particular, a one-sample
        // request must not return zero (which the caller treats as EOF), and
        // the right sample must be delivered before the next frame's left.
        var oddThree = AudioGraphFactory.NormalizeSong(new ChunkedSamples(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 3),
            [0f, 0f, 1f, 0.25f, -0.25f, 0f, 0.5f, -0.5f, 0f], 2));
        float[] oddOut = new float[6];
        int oddPosition = 0;
        // Short reads are legal: a chunked decoder need not fill the entire
        // requested output, but must never report EOF while frames remain.
        foreach (int requested in new[] { 1, 1, 3, 1, 1 })
        {
            int returned = oddThree.Read(oddOut, oddPosition, requested);
            if (returned <= 0 || returned > requested)
                throw new InvalidOperationException(
                    $"Odd 3ch read returned {returned} for request {requested}.");
            oddPosition += returned;
        }
        if (oddPosition != oddOut.Length)
            throw new InvalidOperationException(
                $"Odd 3ch read produced {oddPosition} of {oddOut.Length} samples.");
        Near(0.70710678f, oddOut[0], "odd 3ch first left");
        Near(0.70710678f, oddOut[1], "odd 3ch first right");
        Near(0.25f, oddOut[2], "odd 3ch second left");
        Near(-0.25f, oddOut[3], "odd 3ch second right");
        Near(0.5f, oddOut[4], "odd 3ch third left");
        Near(-0.5f, oddOut[5], "odd 3ch third right");
        if (oddThree.Read(oddOut, 0, 1) != 0)
            throw new InvalidOperationException("Odd 3ch source must reach EOF.");

        // With a full-frame source, an odd request can fill exactly 3
        // samples, leaving the fourth for the next call.
        var fullOdd = AudioGraphFactory.NormalizeSong(new FixedSamples(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 3),
            [0f, 0f, 1f, 0.25f, -0.25f, 0f]));
        float[] fullOddOut = new float[4];
        if (fullOdd.Read(fullOddOut, 0, 3) != 3 ||
            fullOdd.Read(fullOddOut, 3, 1) != 1)
            throw new InvalidOperationException("Full-frame odd request lost a sample.");
        Near(0.70710678f, fullOddOut[0], "full odd first left");
        Near(0.70710678f, fullOddOut[1], "full odd first right");
        Near(0.25f, fullOddOut[2], "full odd second left");
        Near(-0.25f, fullOddOut[3], "full odd second right");

        // One odd-sized request can also end halfway through the final frame.
        var oddSix = AudioGraphFactory.NormalizeSong(new ChunkedSamples(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 6),
            [0f, 0f, 0f, 0f, 0f, 1f], 4));
        float[] oddSixOut = new float[2];
        if (oddSix.Read(oddSixOut, 0, 1) != 1 ||
            oddSix.Read(oddSixOut, 1, 1) != 1 ||
            oddSix.Read(oddSixOut, 0, 1) != 0)
            throw new InvalidOperationException("Odd 6ch reads lost a sample.");
        Near(0.70710678f / 4f, oddSixOut[0], "odd 6ch left");
        Near(0.70710678f / 4f, oddSixOut[1], "odd 6ch right");

        // A six-channel frame may also arrive as 4+2 samples.
        var chunkedSix = AudioGraphFactory.NormalizeSong(new ChunkedSamples(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 6),
            [0f, 0f, 0f, 0f, 0f, 1f], 4));
        float[] chunkedSixOut = new float[2];
        if (chunkedSix.Read(chunkedSixOut, 0, 2) != 2)
            throw new InvalidOperationException("Chunked 6ch frame was lost.");
        Near(0.70710678f / 4f, chunkedSixOut[0], "chunked 6ch left");
        Near(0.70710678f / 4f, chunkedSixOut[1], "chunked 6ch right");

        // A short read can leave one sample of the next 3ch frame pending.
        // Growing the input buffer on the next, larger request must preserve it.
        var resizedThree = AudioGraphFactory.NormalizeSong(new ChunkedSamples(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 3),
            [0.1f, 0.2f, 0f, 0.3f, 0.4f, 0f, 0.5f, 0.6f, 0f], 4));
        float[] resizedOut = new float[8];
        if (resizedThree.Read(resizedOut, 0, 4) != 2 ||
            resizedThree.Read(resizedOut, 2, 6) != 2 ||
            resizedThree.Read(resizedOut, 4, 4) != 2)
            throw new InvalidOperationException("Growing the 3ch input buffer lost a partial frame.");
        Near(0.1f, resizedOut[0], "resized first left");
        Near(0.2f, resizedOut[1], "resized first right");
        Near(0.3f, resizedOut[2], "resized second left");
        Near(0.4f, resizedOut[3], "resized second right");
        Near(0.5f, resizedOut[4], "resized third left");
        Near(0.6f, resizedOut[5], "resized third right");
        if (resizedThree.Read(resizedOut, 0, 2) != 0)
            throw new InvalidOperationException("Resized 3ch source must reach EOF.");

        // An incomplete final frame must never become an output frame.
        var truncated = AudioGraphFactory.NormalizeSong(new ChunkedSamples(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 3),
            [0.25f, -0.25f], 1));
        if (truncated.Read(new float[2], 0, 2) != 0)
            throw new InvalidOperationException("Incomplete final frame was emitted.");

        // Existing mono duplication must remain intact.
        var mono = AudioGraphFactory.NormalizeSong(new FixedSamples(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1), [0.4f]));
        float[] monoOut = new float[2];
        if (mono.Read(monoOut, 0, 2) != 2)
            throw new InvalidOperationException("Mono frame count changed.");
        Near(0.4f, monoOut[0], "mono left");
        Near(0.4f, monoOut[1], "mono right");
    }

    private static void Near(float expected, float actual, string name)
    {
        if (Math.Abs(expected - actual) > 0.00001f)
            throw new InvalidOperationException(
                $"{name}: expected {expected}, actual {actual}");
    }

    private sealed class ChunkedSamples : ISampleProvider
    {
        private readonly float[] _samples;
        private readonly int _maxPerRead;
        private int _position;

        internal ChunkedSamples(WaveFormat waveFormat, float[] samples, int maxPerRead)
        {
            WaveFormat = waveFormat;
            _samples = samples;
            _maxPerRead = maxPerRead;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = Math.Min(Math.Min(count, _maxPerRead), _samples.Length - _position);
            Array.Copy(_samples, _position, buffer, offset, read);
            _position += read;
            return read;
        }
    }

    private sealed class FixedSamples : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        internal FixedSamples(WaveFormat waveFormat, float[] samples)
        {
            WaveFormat = waveFormat;
            _samples = samples;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = Math.Min(count, _samples.Length - _position);
            Array.Copy(_samples, _position, buffer, offset, read);
            _position += read;
            return read;
        }
    }
}

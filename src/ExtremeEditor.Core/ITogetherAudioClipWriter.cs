namespace ExtremeEditor.Core;

/// <summary>
/// A half-open audio-frame interval to decode into an independent, locally
/// playable WAV. All frame coordinates use the decoded original's sample rate.
/// </summary>
public sealed record TogetherAudioClipRequest(
    string DestinationPath,
    long StartSample,
    long EndSampleExclusive);

/// <summary>
/// Keeps audio decoding out of Core. Implementations must use exact decoded
/// frames (not lossy compressed seek timestamps) and reject truncated audio.
/// </summary>
public interface ITogetherAudioClipWriter
{
    void WriteClips(
        string sourceAudioPath,
        int sampleRate,
        IReadOnlyList<TogetherAudioClipRequest> clips);
}

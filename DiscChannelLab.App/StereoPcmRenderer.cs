namespace Disc2Flac;

public interface IPreviewPcmRenderer : IDisposable
{
    IReadOnlyList<float[]> ChannelLeft { get; }
    IReadOnlyList<float[]> ChannelRight { get; }
    long ClippedSamples { get; }
    int TailBlocks { get; }
    float PostSample(int channel, int frame);
    void Render(float[][] input, float[] output, HeadPose pose, HeadphoneOptions options, PreviewMixState state, double master);
}

/// <summary>Ordinary stereo downmix on the dedicated audio thread. No HRTF,
/// directional filtering, headphone headroom, or pose processing is applied.</summary>
public sealed class StereoPcmRenderer : IPreviewPcmRenderer
{
    readonly string[] channels;
    readonly double[] left, right, targetLeft, targetRight;
    readonly float[][] channelLeft, channelRight, post;
    PreviewMixState? active;
    double gain, targetGain, gainStep;
    int mixRamp, gainRamp;
    bool first = true;
    public IReadOnlyList<float[]> ChannelLeft => channelLeft;
    public IReadOnlyList<float[]> ChannelRight => channelRight;
    public long ClippedSamples { get; private set; }
    public int TailBlocks => 0;
    public float PostSample(int channel, int frame) => post[channel][frame];
    public StereoPcmRenderer(string[] channels)
    {
        this.channels = channels;
        left = new double[channels.Length]; right = new double[channels.Length];
        targetLeft = new double[channels.Length]; targetRight = new double[channels.Length];
        float[][] Buffers() => channels.Select(_ => new float[HeadphoneRenderer.BlockSize]).ToArray();
        channelLeft = Buffers(); channelRight = Buffers(); post = Buffers();
    }
    public void Render(float[][] input, float[] output, HeadPose pose, HeadphoneOptions options, PreviewMixState state, double master)
    {
        if (input.Length != channels.Length || input.Any(c => c.Length != HeadphoneRenderer.BlockSize) || output.Length != HeadphoneRenderer.BlockSize * 2)
            throw new ArgumentException("Invalid stereo PCM block.");
        if (state != active)
        {
            StereoPreviewMixer.BuildWeights(channels, state, targetLeft, targetRight);
            active = state;
            if (first) { targetLeft.CopyTo(left, 0); targetRight.CopyTo(right, 0); }
            else mixRamp = 480;
        }
        var requested = double.IsFinite(master) ? Math.Clamp(master, 0, 2) : 1;
        if (first) gain = targetGain = requested;
        if (requested != targetGain) { targetGain = requested; gainRamp = 480; gainStep = (targetGain - gain) / gainRamp; }
        for (int f = 0; f < HeadphoneRenderer.BlockSize; f++)
        {
            if (gainRamp > 0) { gain += gainStep; if (--gainRamp == 0) gain = targetGain; }
            double l = 0, r = 0;
            for (int c = 0; c < channels.Length; c++)
            {
                if (mixRamp > 0) { left[c] += (targetLeft[c] - left[c]) / mixRamp; right[c] += (targetRight[c] - right[c]) / mixRamp; }
                var sample = float.IsFinite(input[c][f]) ? input[c][f] : 0;
                channelLeft[c][f] = (float)(sample * left[c] * gain);
                channelRight[c][f] = (float)(sample * right[c] * gain);
                post[c][f] = (float)(sample * Math.Sqrt(left[c] * left[c] + right[c] * right[c]) * gain);
                l += channelLeft[c][f]; r += channelRight[c][f];
            }
            if (mixRamp > 0) mixRamp--;
            if (Math.Abs(l) > 1) ClippedSamples++;
            if (Math.Abs(r) > 1) ClippedSamples++;
            output[f * 2] = (float)Math.Clamp(l, -1, 1);
            output[f * 2 + 1] = (float)Math.Clamp(r, -1, 1);
        }
        first = false;
    }
    public void Dispose() { }
}

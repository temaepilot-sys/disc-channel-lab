using System.Numerics;

namespace Disc2Flac;

/// <summary>DC-unity 1-pole/1-zero shelf, with sample-wise coefficient transitions.</summary>
public sealed class DirectionalShelf
{
    private double _b0 = 1, _b1, _a1, _x, _y, _db0, _db1, _da1;
    private int _remaining;
    public void Set(double highGain, double k, int ramp = 256)
    {
        k = Math.Clamp(k, .0001, 10); highGain = Math.Clamp(highGain, .01, 3);
        var b0 = (highGain + k) / (1 + k); var b1 = (k - highGain) / (1 + k); var a1 = (k - 1) / (1 + k);
        if (ramp == 0) { _b0 = b0; _b1 = b1; _a1 = a1; _remaining = 0; return; }
        _remaining = ramp; _db0 = (b0 - _b0) / ramp; _db1 = (b1 - _b1) / ramp; _da1 = (a1 - _a1) / ramp;
    }
    public float Process(float sample)
    {
        if (_remaining > 0) { _b0 += _db0; _b1 += _db1; _a1 += _da1; _remaining--; }
        var output = _b0 * sample + _b1 * _x - _a1 * _y;
        _x = sample; _y = Math.Abs(output) < 1e-25 ? 0 : output;
        return (float)_y;
    }
}

public sealed class FractionalEarDelay
{
    private readonly float[] _history = new float[128]; private int _position;
    public float Process(float value, double delay)
    {
        _history[_position] = value;
        delay = Math.Clamp(delay, 0, 100);
        var whole = (int)delay; var fraction = (float)(delay - whole);
        var first = (_position - whole + _history.Length) % _history.Length;
        var second = (first - 1 + _history.Length) % _history.Length;
        var output = _history[first] * (1 - fraction) + _history[second] * fraction;
        _position = (_position + 1) % _history.Length;
        return output;
    }
}

public interface IHeadphoneSpatializer : IDisposable
{
    int TailSamples => 0;
    void Process(int channel, float[] input, float[] left, float[] right, Vector3 direction, HeadphoneOptions options);
}

public sealed class DirectHeadphoneSpatializer : IHeadphoneSpatializer
{
    private readonly DirectionalShelf[] _headLeft, _headRight, _rearLeft, _rearRight;
    private readonly FractionalEarDelay[] _delayLeft, _delayRight;
    public DirectHeadphoneSpatializer(int channels)
    {
        DirectionalShelf[] Shelves() => Enumerable.Range(0, channels).Select(_ => new DirectionalShelf()).ToArray();
        _headLeft = Shelves(); _headRight = Shelves(); _rearLeft = Shelves(); _rearRight = Shelves();
        _delayLeft = Enumerable.Range(0, channels).Select(_ => new FractionalEarDelay()).ToArray();
        _delayRight = Enumerable.Range(0, channels).Select(_ => new FractionalEarDelay()).ToArray();
    }
    public static double HeadAlpha(double earIncidenceRadians, double strength)
    {
        // Brown & Duda (1998), Eq. 5: alpha_min=.1, theta_min=150 degrees.
        // H(s) = (alpha*s + 2*c/a)/(s + 2*c/a). Unlike the ear model, the
        // independent rear shelf below is an adjustable perceptual approximation.
        var alpha = 1.05 + .95 * Math.Cos(earIncidenceRadians / (150 * Math.PI / 180) * Math.PI);
        return 1 + strength * (alpha - 1);
    }
    public void Process(int channel, float[] input, float[] left, float[] right, Vector3 direction, HeadphoneOptions options)
    {
        var pan = HeadphoneGeometry.Pan(direction);
        var earL = Math.Acos(Math.Clamp(-direction.X, -1, 1)); var earR = Math.Acos(Math.Clamp(direction.X, -1, 1));
        var headK = 343 / (options.HeadRadius * HeadphoneRenderer.SampleRate);
        _headLeft[channel].Set(HeadAlpha(earL, options.ShadowStrength), headK);
        _headRight[channel].Set(HeadAlpha(earR, options.ShadowStrength), headK);
        var rearAmount = Math.Pow(Math.Max(0, -direction.Z), 2);
        var rearGain = Math.Pow(10, -options.RearCutDb * rearAmount / 20);
        var rearK = Math.Tan(Math.PI * options.RearFrequency / HeadphoneRenderer.SampleRate);
        _rearLeft[channel].Set(rearGain, rearK); _rearRight[channel].Set(rearGain, rearK);
        var delay = options.InterauralDelay ? HeadphoneGeometry.WoodworthDelay(direction, options.HeadRadius) * HeadphoneRenderer.SampleRate : 0;
        for (var i = 0; i < input.Length; i++)
        {
            var l = _rearLeft[channel].Process(_headLeft[channel].Process(input[i]));
            var r = _rearRight[channel].Process(_headRight[channel].Process(input[i]));
            left[i] = (float)(_delayLeft[channel].Process(l, Math.Max(0, delay)) * pan.Left);
            right[i] = (float)(_delayRight[channel].Process(r, Math.Max(0, -delay)) * pan.Right);
        }
    }
    public void Dispose() { }
}

public sealed class HeadphoneRenderer : IDisposable
{
    public const int SampleRate = 48000, BlockSize = 256;
    private readonly string[] _channels;
    private readonly HeadphoneMethod _method;
    private readonly HeadphoneHrtf _hrtf;
    private readonly IHeadphoneSpatializer _engine;
    private readonly float[][] _left, _right, _scaled;
    private readonly double[] _gains;
    private Quaternion _pose = Quaternion.Identity;
    private bool _first = true;
    private double _master;
    private double _frontAngle = 30;
    private readonly float[] _envelope = new float[BlockSize];
    public IReadOnlyList<float[]> ChannelLeft => _left;
    public IReadOnlyList<float[]> ChannelRight => _right;
    public long ClippedSamples { get; private set; }
    public int TailBlocks => Math.Max(4, (_engine.TailSamples + BlockSize - 1) / BlockSize);
    public HeadphoneRenderer(string[] channels, HeadphoneOptions options)
    {
        _channels = channels; _method = options.Method; _hrtf = options.Hrtf;
        _engine = _method == HeadphoneMethod.SteamAudio ? new SteamHeadphoneSpatializer(channels.Length, options) : new DirectHeadphoneSpatializer(channels.Length);
        _left = channels.Select(_ => new float[BlockSize]).ToArray(); _right = channels.Select(_ => new float[BlockSize]).ToArray();
        _scaled = channels.Select(_ => new float[BlockSize]).ToArray(); _gains = new double[channels.Length];
    }
    public void Render(float[][] input, float[] output, HeadPose pose, HeadphoneOptions options, PreviewMixState state, double master)
    {
        if (input.Length != _channels.Length || input.Any(c => c.Length != BlockSize) || output.Length != BlockSize * 2) throw new ArgumentException("Invalid headphone PCM block.");
        var time = options.SmoothingMs / 1000;
        var blend = _first || time <= 0 ? 1 : 1 - Math.Exp(-BlockSize / (SampleRate * time));
        _pose = Quaternion.Slerp(_pose, pose.Rotation, (float)blend);
        _frontAngle = _first ? options.FrontAngle : _frontAngle + (options.FrontAngle - _frontAngle) *
            (1 - Math.Exp(-BlockSize / (SampleRate * .008)));
        var hasSolo = state.Channels?.Any(c => c.Solo) == true;
        Array.Clear(output);
        var trim = _method == HeadphoneMethod.Direct ? options.DirectTrimDb : _hrtf == HeadphoneHrtf.Sofa ? options.SofaTrimDb : options.SteamTrimDb;
        var global = Math.Pow(10, (options.HeadroomDb + trim) / 20) * Math.Clamp(double.IsFinite(master) ? master : 1, 0, 2);
        if (_first) _master = global;
        for (var frame = 0; frame < BlockSize; frame++)
        {
            _master += (global - _master) / 240;
            _envelope[frame] = (float)_master;
        }
        for (var channel = 0; channel < _channels.Length; channel++)
        {
            var code = _channels[channel]; var setting = state.Channels?.FirstOrDefault(c => c.Code == code);
            var gain = state.Channels is null ? 1 : setting is null || setting.Muted || hasSolo && !setting.Solo ? 0 : Math.Clamp(double.IsFinite(setting.Gain) ? setting.Gain : 0, 0, 1) * (setting.InvertPolarity ? -1 : 1);
            if (state.SoloChannel is { } solo && solo != code) gain = 0;
            if (code == "LFE") gain *= options.LfeGain;
            if (_first) _gains[channel] = gain;
            for (var frame = 0; frame < BlockSize; frame++)
            {
                _gains[channel] += (gain - _gains[channel]) / 240;
                var value = input[channel][frame];
                _scaled[channel][frame] = (float)((float.IsFinite(value) ? value : 0) * _gains[channel]);
            }
            if (code == "LFE")
                for (int frame = 0; frame < BlockSize; frame++) _left[channel][frame] = _right[channel][frame] = _scaled[channel][frame] * .70710678f;
            else _engine.Process(channel, _scaled[channel], _left[channel], _right[channel], HeadphoneGeometry.Relative(code, _pose, _channels, _frontAngle), options);
            for (var frame = 0; frame < BlockSize; frame++)
            {
                _left[channel][frame] *= _envelope[frame]; _right[channel][frame] *= _envelope[frame];
                output[2 * frame] += _left[channel][frame]; output[2 * frame + 1] += _right[channel][frame];
            }
        }
        for (var i = 0; i < output.Length; i++)
        {
            if (!float.IsFinite(output[i])) output[i] = 0;
            if (Math.Abs(output[i]) > 1) { ClippedSamples++; output[i] = Math.Clamp(output[i], -1, 1); }
        }
        _first = false;
    }
    public void Dispose() => _engine.Dispose();
}

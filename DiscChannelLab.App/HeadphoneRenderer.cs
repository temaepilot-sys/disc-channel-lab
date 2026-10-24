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
    void Flush(int channel, float[] left, float[] right) { Array.Clear(left); Array.Clear(right); }
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

public sealed class HeadphoneRenderer : IPreviewPcmRenderer
{
    public const int SampleRate = 48000, BlockSize = 256;
    private readonly string[] _channels;
    private readonly HeadphoneMethod _method;
    private readonly HeadphoneHrtf _hrtf;
    private readonly IHeadphoneSpatializer _engine;
    private SpatialScene? _scene;
    private readonly EarlyReflections? _reflections;
    private readonly bool _clampOutput;
    private long _samplePosition;
    private bool _tailMode;
    private int _reflectionTailBlocks;
    private readonly float[][] _left, _right, _scaled;
    private readonly double[] _gains;
    private Quaternion _pose = Quaternion.Identity;
    private bool _first = true;
    private double _master;
    private double _frontAngle = 30;
    private readonly float[] _envelope = new float[BlockSize];
    public IReadOnlyList<float[]> ChannelLeft => _left;
    public IReadOnlyList<float[]> ChannelRight => _right;
    public float PostSample(int channel, int frame) => (_left[channel][frame] + _right[channel][frame]) * .70710678f;
    public long ClippedSamples { get; private set; }
    public int TailBlocks => Math.Max(4, (_engine.TailSamples + (_reflections?.TailFrames ?? 0) + BlockSize - 1) / BlockSize);
    public void BeginTail() { _tailMode = true; _reflectionTailBlocks = ((_reflections?.TailFrames ?? 0) + 255) / 256; }
    public void UpdateScene(SpatialScene scene)
    {
        if (ReferenceEquals(_scene, scene)) return;
        if (scene.Sources.Length != _channels.Length) throw new InvalidDataException("Live scene/source count changed.");
        for (int i = 0; i < _channels.Length; i++)
            if (scene.Sources[i].Id != _channels[i]) throw new InvalidDataException("Live scene/source identity changed.");
        _scene = scene;
    }
    public HeadphoneRenderer(string[] channels, HeadphoneOptions options, SpatialScene? scene = null, bool clampOutput = true, long startSample = 0)
    {
        _channels = channels; _method = options.Method; _hrtf = options.Hrtf; _scene = scene; _clampOutput = clampOutput; _samplePosition = startSample;
        if (scene is not null && scene.Sources.Length != channels.Length) throw new ArgumentException("Scene/source count differs.");
        options.Room.Validate();
        if (options.Room.Enabled && _method != HeadphoneMethod.MeasuredBrir)
        {
            for (int i = 0; i < channels.Length; i++)
            {
                if (scene is null ? channels[i] == "LFE" : scene.Sources[i].IsLfe) continue;
                var positions = scene is null ? new[] { HeadphoneGeometry.Speaker(channels[i], channels, options.FrontAngle) }
                    : scene.Sources[i].Blocks.Select(SpatialSource.ToWorld);
                foreach (var position in positions) EarlyReflections.Path(position * (float)options.Room.SpeakerRadius, options.Room, 0);
            }
            _reflections = new(channels.Length, options.Room);
        }
        int effects = channels.Length * (_reflections is null ? 1 : 7);
        _engine = _method switch { HeadphoneMethod.SteamAudio => new SteamHeadphoneSpatializer(effects, options),
            HeadphoneMethod.MeasuredBrir => new BrirSpatializer(channels, options, scene), _ => new DirectHeadphoneSpatializer(effects) };
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
        var trim = _method == HeadphoneMethod.MeasuredBrir ? options.BrirTrimDb : _method == HeadphoneMethod.Direct ? options.DirectTrimDb : _hrtf == HeadphoneHrtf.Sofa ? options.SofaTrimDb : options.SteamTrimDb;
        var global = Math.Pow(10, (options.HeadroomDb + trim) / 20) * Math.Clamp(double.IsFinite(master) ? master : 1, 0, 2);
        if (_first) _master = global;
        for (var frame = 0; frame < BlockSize; frame++)
        {
            _master += (global - _master) / 240;
            _envelope[frame] = (float)_master;
        }
        for (var channel = 0; channel < _channels.Length; channel++)
        {
            var code = _channels[channel]; ExperimentalChannel? setting = null;
            if (state.Channels is { } strips) for (int i = 0; i < strips.Length; i++) if (strips[i].Code == code) { setting = strips[i]; break; }
            var gain = state.Channels is null ? 1 : setting is null || setting.Muted || hasSolo && !setting.Solo ? 0 : Math.Clamp(double.IsFinite(setting.Gain) ? setting.Gain : 0, 0, 1) * (setting.InvertPolarity ? -1 : 1);
            if (state.SoloChannel is { } solo && solo != code) gain = 0;
            bool isLfe = _scene is null ? code == "LFE" : _scene.Sources[channel].IsLfe;
            if (isLfe) gain *= options.LfeGain;
            if (_first) _gains[channel] = gain;
            for (var frame = 0; frame < BlockSize; frame++)
            {
                _gains[channel] += (gain - _gains[channel]) / 240;
                var value = input[channel][frame];
                float sceneGain = _scene?.Sources[channel].GainAt(_samplePosition + frame) ?? 1;
                _scaled[channel][frame] = (float)((float.IsFinite(value) ? value : 0) * _gains[channel] * sceneGain);
            }
            if (isLfe && _method != HeadphoneMethod.MeasuredBrir)
                for (int frame = 0; frame < BlockSize; frame++) _left[channel][frame] = _right[channel][frame] =
                    _reflections is not null && options.Room.SoloReflections ? 0 : _scaled[channel][frame] * .70710678f;
            else
            {
                var world = _scene?.Sources[channel].At(_samplePosition + BlockSize / 2).Position ?? HeadphoneGeometry.Speaker(code, _channels, _frontAngle);
                var direction = world.LengthSquared() < 1e-10 ? Vector3.UnitZ : Vector3.Normalize(world);
                if (_tailMode && _method == HeadphoneMethod.SteamAudio) _engine.Flush(channel, _left[channel], _right[channel]);
                else _engine.Process(channel, _scaled[channel], _left[channel], _right[channel], Vector3.Transform(direction, Quaternion.Conjugate(_pose)), options);
                if (_reflections is not null)
                {
                    if (options.Room.SoloReflections) { Array.Clear(_left[channel]); Array.Clear(_right[channel]); }
                    if (_tailMode && _reflectionTailBlocks <= 0 && _method == HeadphoneMethod.SteamAudio)
                        _reflections.Flush(channel, _left[channel], _right[channel], _engine, _channels.Length);
                    else _reflections.Add(channel, _scaled[channel], _left[channel], _right[channel], world * (float)options.Room.SpeakerRadius, _pose, options, _engine, _channels.Length);
                }
            }
            for (var frame = 0; frame < BlockSize; frame++)
            {
                _left[channel][frame] *= _envelope[frame]; _right[channel][frame] *= _envelope[frame];
                output[2 * frame] += _left[channel][frame]; output[2 * frame + 1] += _right[channel][frame];
            }
        }
        for (var i = 0; i < output.Length; i++)
        {
            if (!float.IsFinite(output[i])) output[i] = 0;
            if (Math.Abs(output[i]) > 1) { ClippedSamples++; if (_clampOutput) output[i] = Math.Clamp(output[i], -1, 1); }
        }
        _first = false;
        _samplePosition += BlockSize;
        if (_tailMode) _reflectionTailBlocks--;
    }
    public void Dispose() => _engine.Dispose();
}

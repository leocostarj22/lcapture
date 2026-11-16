using NAudio.Wave;

namespace LCapture.Services;

/// <summary>
/// Mixes any number of audio sources (capture card, a window's app sound…) into one continuous
/// 48 kHz stereo float track for the recorder. The mixer runs on its own clock (the
/// <see cref="MediaClock"/>), a little behind real time, so the track never has gaps even when a
/// source goes quiet, stops sending packets or is added/removed mid-recording.
/// </summary>
public sealed class AudioMixer : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    /// <summary>How far behind real time the mix runs: room for packets to arrive.</summary>
    internal static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(100);

    public delegate void MixedHandler(byte[] buffer, int count, TimeSpan time);

    private readonly object _lock = new();
    private readonly List<MixerInput> _inputs = [];
    private readonly MixedHandler _output;
    private readonly TimeSpan _start;
    private readonly Thread _thread;
    private volatile bool _running = true;
    private long _framesOut;
    private float[] _mix = new float[SampleRate / 10 * Channels];
    private byte[] _bytes = new byte[SampleRate / 10 * Channels * 4];

    /// <param name="start">Time zero of the track, on the MediaClock timeline.</param>
    public AudioMixer(TimeSpan start, MixedHandler output)
    {
        _start = start;
        _output = output;
        _thread = new Thread(Run) { IsBackground = true, Name = "LCapture audio mixer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public static bool CanMix(WaveFormat format) => MixerInput.ReadSample(format) is not null;

    public MixerInput AddInput(WaveFormat format)
    {
        var input = new MixerInput(format);
        lock (_lock) _inputs.Add(input);
        return input;
    }

    public void RemoveInput(MixerInput? input)
    {
        if (input is null) return;
        lock (_lock) _inputs.Remove(input);
    }

    private void Run()
    {
        while (_running)
        {
            Thread.Sleep(10);
            try
            {
                Pump();
            }
            catch
            {
                // Never let a bad packet kill the recording's audio thread.
            }
        }
    }

    private void Pump()
    {
        long target = (long)((MediaClock.Now - _start - Delay).TotalSeconds * SampleRate);
        long frames = target - _framesOut;
        if (frames <= 0) return;
        if (frames > SampleRate) frames = SampleRate; // After a stall, don't emit a huge block at once.

        int count = (int)frames * Channels;
        if (_mix.Length < count)
        {
            _mix = new float[count];
            _bytes = new byte[count * 4];
        }
        Array.Clear(_mix, 0, count);

        lock (_lock)
        {
            foreach (var input in _inputs)
                input.ReadInto(_mix, (int)frames);
        }

        for (int i = 0; i < count; i++)
            _mix[i] = Math.Clamp(_mix[i], -1f, 1f);
        Buffer.BlockCopy(_mix, 0, _bytes, 0, count * 4);

        var time = TimeSpan.FromSeconds((double)_framesOut / SampleRate);
        _framesOut += frames;
        _output(_bytes, count * 4, time);
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(500);
    }
}

/// <summary>One source of the mixer: converts its packets to 48 kHz stereo float and buffers them.</summary>
public sealed class MixerInput
{
    private static readonly int DelayFrames = (int)(AudioMixer.Delay.TotalSeconds * AudioMixer.SampleRate);
    private static readonly int MaxFrames = DelayFrames + AudioMixer.SampleRate / 10; // +100 ms

    private readonly object _lock = new();
    private readonly WaveFormat _format;
    private readonly Func<byte[], int, float>? _read;
    private readonly int _bytesPerSample;
    private readonly double _step; // Input frames per output frame.

    // Ring buffer of interleaved stereo samples at 48 kHz.
    private readonly float[] _ring = new float[AudioMixer.SampleRate * 2 * AudioMixer.Channels];
    private int _head, _frames;
    private bool _primed;

    // Resampler state: position between the previous input frame (index 0) and the next ones.
    private double _pos;
    private float _prevL, _prevR;

    private volatile float _gain = 1f;

    internal MixerInput(WaveFormat format)
    {
        _format = format;
        _read = ReadSample(format);
        _bytesPerSample = format.BitsPerSample / 8;
        _step = (double)format.SampleRate / AudioMixer.SampleRate;
    }

    /// <summary>Volume applied to this source in the recording (0 = silent).</summary>
    public float Gain
    {
        get => _gain;
        set => _gain = Math.Clamp(value, 0f, 2f);
    }

    internal static Func<byte[], int, float>? ReadSample(WaveFormat format)
    {
        var encoding = format.Encoding;
        if (format is WaveFormatExtensible ext)
        {
            if (ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")) encoding = WaveFormatEncoding.IeeeFloat;
            else if (ext.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71")) encoding = WaveFormatEncoding.Pcm;
        }

        return (encoding, format.BitsPerSample) switch
        {
            (WaveFormatEncoding.IeeeFloat, 32) => (b, i) => BitConverter.ToSingle(b, i),
            (WaveFormatEncoding.Pcm, 16) => (b, i) => BitConverter.ToInt16(b, i) / 32768f,
            (WaveFormatEncoding.Pcm, 24) => (b, i) => ((b[i] | b[i + 1] << 8 | (sbyte)b[i + 2] << 16)) / 8388608f,
            (WaveFormatEncoding.Pcm, 32) => (b, i) => BitConverter.ToInt32(b, i) / 2147483648f,
            _ => null,
        };
    }

    /// <summary>Adds a captured packet (any thread).</summary>
    public void Write(byte[] data, int count)
    {
        if (_read is null || count <= 0) return;

        int channels = _format.Channels;
        int blockAlign = _format.BlockAlign;
        int inFrames = count / blockAlign;

        lock (_lock)
        {
            for (int f = 0; f < inFrames; f++)
            {
                int offset = f * blockAlign;
                float l = _read(data, offset);
                float r = channels > 1 ? _read(data, offset + _bytesPerSample) : l;

                if (_step == 1.0)
                {
                    Push(l, r);
                    continue;
                }

                // Linear resampling between the previous frame and this one.
                while (_pos < 1.0)
                {
                    float t = (float)_pos;
                    Push(_prevL + (l - _prevL) * t, _prevR + (r - _prevR) * t);
                    _pos += _step;
                }
                _pos -= 1.0;
                _prevL = l;
                _prevR = r;
            }

            // The source's clock runs slightly faster than ours: drop the excess so the delay
            // (and with it the sync with the picture) stays where it should be.
            if (_frames > MaxFrames)
            {
                int drop = _frames - DelayFrames;
                _head = (_head + drop * 2) % _ring.Length;
                _frames -= drop;
            }
        }
    }

    private void Push(float l, float r)
    {
        int capacity = _ring.Length / 2;
        if (_frames == capacity)
        {
            _head = (_head + 2) % _ring.Length; // Full: drop the oldest frame.
            _frames--;
        }
        int tail = (_head + _frames * 2) % _ring.Length;
        _ring[tail] = l;
        _ring[tail + 1] = r;
        _frames++;
    }

    /// <summary>Adds up to <paramref name="frames"/> buffered frames to the mix (mixer thread).</summary>
    internal void ReadInto(float[] mix, int frames)
    {
        float gain = _gain;
        lock (_lock)
        {
            // Wait until the delay's worth of sound is buffered, so this source lines up with
            // the mixer's timeline. Re-done whenever the source runs dry (it stopped sending).
            if (!_primed)
            {
                if (_frames < DelayFrames) return;
                _primed = true;
            }

            int available = Math.Min(_frames, frames);
            for (int i = 0; i < available; i++)
            {
                mix[i * 2] += _ring[_head] * gain;
                mix[i * 2 + 1] += _ring[_head + 1] * gain;
                _head = (_head + 2) % _ring.Length;
            }
            _frames -= available;

            if (available < frames)
                _primed = false;
        }
    }
}

using System.IO;
using System.Threading.Channels;
using LCapture.Interop;
using LCapture.Models;
using NAudio.Wave;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT;

namespace LCapture.Services;

/// <summary>
/// Records the composed frames and the mixed audio to an H.264/AAC MP4 file. Samples go into a
/// MediaStreamSource that a MediaTranscoder pulls from; Windows picks the GPU's hardware encoder
/// (NVENC, AMF, Quick Sync) when there is one. Video and audio share the <see cref="MediaClock"/>
/// timeline, relative to the moment recording started. The audio track always exists (silent if
/// nothing is attached), so sources can come and go during a recording.
/// </summary>
public sealed class RecordingService
{
    // Media Foundation attribute: a positive stride marks RGB frames as top-down. Without it,
    // MF assumes uncompressed RGB is stored bottom-up and the video comes out upside down.
    private static readonly Guid MfMtDefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    // Uncompressed frames are large (8 MB at 1080p, 33 MB at 4K). If the encoder falls behind,
    // drop frames instead of letting the queue eat the memory.
    private const long MaxQueuedVideoBytes = 512L * 1024 * 1024;

    private readonly Channel<MediaStreamSample> _video;
    private readonly Channel<MediaStreamSample> _audio;
    private readonly IRandomAccessStream _output;
    private readonly VideoFormatInfo _source;
    private readonly TimeSpan _frameInterval;
    private TimeSpan _start;
    private AudioMixer? _mixer;
    private Task _transcode = Task.CompletedTask;

    private readonly object _videoLock = new();
    private TimeSpan _nextFrameDue;
    private long _framesWritten;
    private long _framesDropped;

    private volatile bool _stopping;

    public string FilePath { get; }
    public RecordingProfile Profile { get; }
    public VideoFormatInfo Source => _source;

    public TimeSpan Elapsed => MediaClock.Now - _start;
    public long FramesWritten => Interlocked.Read(ref _framesWritten);
    public long FramesDropped => Interlocked.Read(ref _framesDropped);

    /// <summary>Raised (on a background thread) if encoding stops by itself, e.g. disk full.</summary>
    public event EventHandler<string>? Failed;

    private RecordingService(string path, IRandomAccessStream output, VideoFormatInfo source, RecordingProfile profile)
    {
        FilePath = path;
        Profile = profile;
        _output = output;
        _source = source;
        _frameInterval = TimeSpan.FromSeconds(1 / profile.Fps);

        long frameBytes = (long)source.Width * source.Height * 4;
        int capacity = (int)Math.Clamp(MaxQueuedVideoBytes / Math.Max(frameBytes, 1), 3, 120);
        _video = Channel.CreateBounded<MediaStreamSample>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false, // Main layer can switch sources (threads) mid-recording.
        });
        _audio = Channel.CreateUnbounded<MediaStreamSample>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
    }

    /// <summary>Creates the file and starts encoding frames of <paramref name="source"/>'s size.</summary>
    public static async Task<RecordingService> StartAsync(string path, VideoFormatInfo source, RecordingProfile profile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var output = await FileRandomAccessStream.OpenAsync(
            path, FileAccessMode.ReadWrite, StorageOpenOptions.AllowOnlyReaders, FileOpenDisposition.CreateNew);

        var recorder = new RecordingService(path, output, source, profile);
        try
        {
            await recorder.StartTranscodeAsync();
        }
        catch
        {
            output.Dispose();
            TryDelete(path);
            throw;
        }

        return recorder;
    }

    private async Task StartTranscodeAsync()
    {
        // A regular MP4 (the index is written when recording stops). MediaTranscoder does not
        // offer fragmented MP4, which would survive a crash; that needs the MF SinkWriter.
        var prepared = await PrepareAsync();
        if (!prepared.CanTranscode)
            throw new InvalidOperationException(prepared.FailureReason switch
            {
                TranscodeFailureReason.CodecNotFound =>
                    "o Windows não encontrou um codificador H.264/AAC (Windows N sem o Media Feature Pack?)",
                TranscodeFailureReason.InvalidProfile => "a definição de gravação não é suportada por este PC",
                _ => $"o Windows recusou a gravação ({prepared.FailureReason})",
            });

        // Time zero: frames and audio before this moment are not part of the file.
        _start = MediaClock.Now;
        _mixer = new AudioMixer(_start, OnMixedAudio);
        _transcode = prepared.TranscodeAsync().AsTask();
        _ = _transcode.ContinueWith(t =>
        {
            if (!_stopping)
                Failed?.Invoke(this, t.Exception?.GetBaseException().Message ?? "a codificação parou sozinha");
        }, TaskScheduler.Default);
    }

    private async Task<PrepareTranscodeResult> PrepareAsync()
    {
        // Rgb32 = B,G,R,X in memory: same bytes as our Bgra8 frames, with the alpha byte ignored.
        var videoInput = VideoEncodingProperties.CreateUncompressed(
            MediaEncodingSubtypes.Rgb32, _source.Width, _source.Height);
        videoInput.FrameRate.Numerator = _source.FpsNumerator;
        videoInput.FrameRate.Denominator = Math.Max(_source.FpsDenominator, 1);
        videoInput.PixelAspectRatio.Numerator = 1;
        videoInput.PixelAspectRatio.Denominator = 1;
        videoInput.Properties[MfMtDefaultStride] = _source.Width * 4;

        var audioInput = AudioEncodingProperties.CreatePcm(AudioMixer.SampleRate, AudioMixer.Channels, 32);
        audioInput.Subtype = MediaEncodingSubtypes.Float;

        var mediaSource = new MediaStreamSource(new VideoStreamDescriptor(videoInput), new AudioStreamDescriptor(audioInput))
        {
            BufferTime = TimeSpan.Zero, // Live source: hand samples over as they come.
            CanSeek = false,
        };
        mediaSource.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        mediaSource.SampleRequested += OnSampleRequested;

        var encoding = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        encoding.Video.Width = Profile.Width;
        encoding.Video.Height = Profile.Height;
        encoding.Video.FrameRate.Numerator = Profile.FpsNumerator;
        encoding.Video.FrameRate.Denominator = Profile.FpsDenominator;
        encoding.Video.PixelAspectRatio.Numerator = 1;
        encoding.Video.PixelAspectRatio.Denominator = 1;
        encoding.Video.Bitrate = Profile.VideoBitrate;
        encoding.Audio = AudioEncodingProperties.CreateAac(AudioMixer.SampleRate, AudioMixer.Channels, RecordingProfile.AudioBitrate);

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true, AlwaysReencode = true };
        return await transcoder.PrepareMediaStreamSourceTranscodeAsync(mediaSource, _output, encoding);
    }

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var request = args.Request;
        var channel = request.StreamDescriptor is VideoStreamDescriptor ? _video : _audio;
        if (channel.Reader.TryRead(out var ready))
        {
            request.Sample = ready;
            return;
        }

        // Nothing queued yet: answer when the next sample arrives. A null sample ends the stream.
        var deferral = request.GetDeferral();
        _ = Task.Run(async () =>
        {
            MediaStreamSample? sample = null;
            try
            {
                if (await channel.Reader.WaitToReadAsync())
                    channel.Reader.TryRead(out sample);
            }
            catch
            {
                // Channel faulted: end the stream.
            }

            request.Sample = sample;
            deferral.Complete();
        });
    }

    /// <summary>Attaches an audio source to the recording's track (any time, any thread).</summary>
    public MixerInput? AddAudioInput(WaveFormat format) =>
        _mixer is not null && AudioMixer.CanMix(format) ? _mixer.AddInput(format) : null;

    public void RemoveAudioInput(MixerInput? input) => _mixer?.RemoveInput(input);

    /// <summary>Queues a frame. Called on the capture thread, before the preview takes the frame.</summary>
    public void AddFrame(CapturedFrame frame)
    {
        if (_stopping) return;

        var bitmap = frame.Bitmap;
        if (bitmap.PixelWidth != _source.Width || bitmap.PixelHeight != _source.Height) return;

        var time = frame.Timestamp - _start;
        if (time < TimeSpan.Zero) return;

        lock (_videoLock)
        {
            // Keep one frame per output interval (60 → 30 fps drops every other frame). Half an
            // interval of slack absorbs timestamp jitter at the source rate.
            if (time < _nextFrameDue - _frameInterval / 2) return;
            _nextFrameDue = time - _nextFrameDue > _frameInterval * 2
                ? time + _frameInterval // after a gap (e.g. a still window), resync to the new frame
                : _nextFrameDue + _frameInterval;
        }

        var sample = MediaStreamSample.CreateFromBuffer(CopyPixels(bitmap), time);
        sample.Duration = _frameInterval;
        sample.KeyFrame = true;

        if (_video.Writer.TryWrite(sample))
            Interlocked.Increment(ref _framesWritten);
        else
            Interlocked.Increment(ref _framesDropped);
    }

    private unsafe IBuffer CopyPixels(SoftwareBitmap bitmap)
    {
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        uint size = (uint)width * 4 * (uint)height;
        var target = new Windows.Storage.Streams.Buffer(size) { Length = size };

        using var source = new BitmapLock(bitmap, BitmapBufferAccessMode.Read);
        PixelOps.Copy(source.Data, source.Stride, target.As<IBufferByteAccess>().Buffer(), width * 4, width, height);
        return target;
    }

    private unsafe void OnMixedAudio(byte[] data, int count, TimeSpan time)
    {
        if (_stopping) return;

        var buffer = new Windows.Storage.Streams.Buffer((uint)count) { Length = (uint)count };
        fixed (byte* src = data)
            System.Buffer.MemoryCopy(src, buffer.As<IBufferByteAccess>().Buffer(), count, count);

        var sample = MediaStreamSample.CreateFromBuffer(buffer, time);
        sample.Duration = TimeSpan.FromSeconds((double)count / AudioMixer.Format.AverageBytesPerSecond);
        _audio.Writer.TryWrite(sample);
    }

    /// <summary>Ends the streams, lets the encoder flush what is queued and closes the file.</summary>
    public async Task StopAsync()
    {
        if (_stopping) return;
        _stopping = true;

        _mixer?.Dispose();
        _video.Writer.TryComplete();
        _audio.Writer.TryComplete();

        try
        {
            await _transcode.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            _output.Dispose();
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}

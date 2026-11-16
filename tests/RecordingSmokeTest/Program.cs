// Headless check of the recording pipeline (no capture card needed): records synthetic frames
// (red top, blue bottom, green video layer bottom-right, a full-size picture frame with a yellow
// border and a transparent middle, a 50% white square top-left) and a 440 Hz tone, then reads the MP4 back.
// Usage: dotnet run -c Release --project tests/RecordingSmokeTest -- [width height fps [srcWidth srcHeight]]
// Expect: top=red bottom=blue overlay=green border=yellow half=rgb(255,127,127), dropped=0, RMS≈0.212, ~440 Hz.

using LCapture.Interop;
using LCapture.Models;
using LCapture.Services;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using NAudio.Wave;

int W = args.Length > 0 ? int.Parse(args[0]) : 1280, H = args.Length > 1 ? int.Parse(args[1]) : 720;
int Fps = args.Length > 2 ? int.Parse(args[2]) : 30;
int SrcW = args.Length > 3 ? int.Parse(args[3]) : W, SrcH = args.Length > 4 ? int.Parse(args[4]) : H;
Console.WriteLine($"Hardware H.264 encoder: {MediaFoundationInterop.HasHardwareH264Encoder()}");

var path = Path.Combine(Path.GetTempPath(), $"lcapture-test-{Guid.NewGuid():N}.mp4");
var source = new VideoFormatInfo((uint)W, (uint)H, (uint)Fps, 1);
var profile = RecordingProfile.Resolve(source, new RecordingChoice(0, false, RecordingQuality.Medium));
var recorder = await RecordingService.StartAsync(path, source, profile);
Console.WriteLine($"Started: bitrate={profile.VideoBitrate}");

var compositor = new FrameCompositor { LockedCanvas = (W, H) };
var videoId = Guid.NewGuid(); var frameId = Guid.NewGuid(); var halfId = Guid.NewGuid();
compositor.SetLayers([
    new CompositorLayer(videoId, new RectN(0.70, 0.70, 0.28, 0.28), 1, Border: true),
    new CompositorLayer(frameId, new RectN(0, 0, 1, 1), 1, Border: false),
    new CompositorLayer(halfId, new RectN(0.05, 0.05, 0.10, 0.10), 0.5, Border: false),
]);
// Full-size "frame" picture: opaque yellow 40 px border, transparent middle (premultiplied).
var picture = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 1920, 1080, BitmapAlphaMode.Premultiplied);
Px.FillXY(picture, (x, y) => x < 40 || y < 40 || x >= 1880 || y >= 1040 ? 0xFFFFFF00u : 0u);
compositor.SetPicture(frameId, picture);
var white = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 64, 64, BitmapAlphaMode.Premultiplied);
Px.Fill(white, _ => 0xFFFFFFFFu);
compositor.SetPicture(halfId, white);
// Green overlay frame (640x360).
var green = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 640, 360, BitmapAlphaMode.Ignore);
Px.Fill(green, _ => 0xFF00FF00u);
compositor.SetVideoFrame(videoId, new CapturedFrame(green, null, MediaClock.Now));

var mic = recorder.AddAudioInput(NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(44100, 1)); // exercises resampling + mono
var audioThread = new Thread(() =>
{
    var buf = new byte[441 * 4]; double phase = 0;
    var start = DateTime.UtcNow; long sent = 0;
    while ((DateTime.UtcNow - start).TotalSeconds < 3.3)
    {
        long due = (long)((DateTime.UtcNow - start).TotalSeconds * 44100);
        while (sent + 441 <= due)
        {
            for (int i = 0; i < 441; i++) { BitConverter.TryWriteBytes(buf.AsSpan(i * 4), (float)(0.3 * Math.Sin(phase))); phase += 2 * Math.PI * 440 / 44100; }
            mic!.Write(buf, buf.Length); sent += 441;
        }
        Thread.Sleep(5);
    }
});
audioThread.Start();

var sw = System.Diagnostics.Stopwatch.StartNew();
double composeMs = 0; int frames = Fps * 3;
for (int n = 0; n < frames; n++)
{
    var frame = new SoftwareBitmap(BitmapPixelFormat.Bgra8, SrcW, SrcH, BitmapAlphaMode.Ignore);
    Px.Fill(frame, y => y < SrcH / 2 ? 0xFFFF0000u : 0xFF0000FFu);
    var t0 = sw.Elapsed;
    var composed = compositor.Compose(new CapturedFrame(frame, null, MediaClock.Now));
    recorder.AddFrame(composed);
    composeMs += (sw.Elapsed - t0).TotalMilliseconds;
    composed.Dispose();
    var wait = TimeSpan.FromMilliseconds(1000.0 / Fps * (n + 1)) - sw.Elapsed;
    if (wait > TimeSpan.Zero) Thread.Sleep(wait);
}
audioThread.Join();
await recorder.StopAsync();
Console.WriteLine($"Compose+copy per frame: {composeMs / frames:0.00} ms");
Console.WriteLine($"Stopped: written={recorder.FramesWritten}, dropped={recorder.FramesDropped}, size={new FileInfo(path).Length} bytes");

var file = await StorageFile.GetFileFromPathAsync(path);
var clip = await MediaClip.CreateFromFileAsync(file);
Console.WriteLine($"Duration: {clip.OriginalDuration}, audio tracks: {clip.EmbeddedAudioTracks.Count}");
var props = clip.GetVideoEncodingProperties();
Console.WriteLine($"Video: {props.Subtype} {props.Width}x{props.Height} @ {props.FrameRate.Numerator}/{props.FrameRate.Denominator}");
var audio = clip.EmbeddedAudioTracks.FirstOrDefault()?.GetAudioEncodingProperties();
if (audio is not null) Console.WriteLine($"Audio: {audio.Subtype} {audio.SampleRate} Hz x{audio.ChannelCount}");

var comp = new MediaComposition(); comp.Clips.Add(clip);
var thumb = await comp.GetThumbnailAsync(TimeSpan.FromSeconds(1.5), W, H, VideoFramePrecision.NearestFrame);
var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(thumb);
var bmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
Console.WriteLine($"Thumbnail {bmp.PixelWidth}x{bmp.PixelHeight}: top={Px.Name(bmp, 100, 50)} bottom={Px.Name(bmp, 100, H - 50)} " +
                  $"overlay(bottom-right)={Px.Name(bmp, W - 100, H - 80)} top-right={Px.Name(bmp, W - 100, 50)} " +
                  $"border={Px.Name(bmp, 5, H / 2)} half={Px.Name(bmp, (int)(W * 0.1), (int)(H * 0.1))}");
Console.WriteLine($"File: {path}");

// Decode the recorded audio to WAV and check it is the 440 Hz tone, not silence.
var wavPath = Path.ChangeExtension(path, ".wav");
var wavFile = await (await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(wavPath)!)).CreateFileAsync(Path.GetFileName(wavPath), CreationCollisionOption.ReplaceExisting);
var wavProfile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateWav(Windows.Media.MediaProperties.AudioEncodingQuality.High);
wavProfile.Video = null;
var prep = await new Windows.Media.Transcoding.MediaTranscoder().PrepareFileTranscodeAsync(file, wavFile, wavProfile);
Console.WriteLine($"WAV transcode possible: {prep.CanTranscode} {prep.FailureReason}");
await prep.TranscodeAsync();
using (var reader = new NAudio.Wave.WaveFileReader(wavPath))
{
    var sp = reader.ToSampleProvider();
    var all = new List<float>(); var block = new float[4096]; int n;
    while ((n = sp.Read(block, 0, block.Length)) > 0) all.AddRange(block.Take(n));
    int ch = reader.WaveFormat.Channels, rate = reader.WaveFormat.SampleRate;
    var left = all.Where((_, i) => i % ch == 0).ToArray();
    // Middle second, to skip the start-up delay.
    var mid = left.Skip(rate).Take(rate).ToArray();
    double rms = Math.Sqrt(mid.Select(v => (double)v * v).Average());
    int crossings = 0; for (int i = 1; i < mid.Length; i++) if (mid[i - 1] < 0 && mid[i] >= 0) crossings++;
    int firstSound = Array.FindIndex(left, v => Math.Abs(v) > 0.05f);
    Console.WriteLine($"Audio: {rate} Hz x{ch}, {left.Length / (double)rate:0.00} s, RMS(1-2 s)={rms:0.000} (sine 0.3 => 0.212), ~{crossings} Hz, sound starts at {firstSound / (double)rate * 1000:0} ms");
}

File.Delete(wavPath);
File.Delete(path);

static unsafe class Px
{
    public static void Fill(SoftwareBitmap b, Func<int, uint> color)
    {
        using var l = new BitmapLock(b, BitmapBufferAccessMode.Write);
        for (int y = 0; y < l.Height; y++) new Span<uint>(l.Data + y * l.Stride, l.Width).Fill(color(y));
    }

    public static void FillXY(SoftwareBitmap b, Func<int, int, uint> color)
    {
        using var l = new BitmapLock(b, BitmapBufferAccessMode.Write);
        for (int y = 0; y < l.Height; y++)
        {
            uint* row = (uint*)(l.Data + y * l.Stride);
            for (int x = 0; x < l.Width; x++) row[x] = color(x, y);
        }
    }

    public static string Name(SoftwareBitmap b, int x, int y)
    {
        using var l = new BitmapLock(b, BitmapBufferAccessMode.Read);
        uint p = *(uint*)(l.Data + y * l.Stride + x * 4);
        int r = (int)(p >> 16 & 255), g = (int)(p >> 8 & 255), bl = (int)(p & 255);
        return r > 150 && g < 100 && bl < 100 ? "red" : bl > 150 && r < 100 && g < 100 ? "blue"
             : g > 150 && r < 100 && bl < 100 ? "green" : r > 200 && g > 200 && bl < 80 ? "yellow" : $"rgb({r},{g},{bl})";
    }
}

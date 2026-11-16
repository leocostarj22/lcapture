using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace LCapture.Services;

/// <summary>
/// Plays a video file (WebM, MP4, MOV…) in a loop, without sound, and delivers its frames like a
/// camera, for an animated layer (webcam frames, logos, effects). Decoding is done by Windows —
/// WebM needs the free "Web Media Extensions" / "VP9 Video Extensions" from the Microsoft Store.
/// Note: Windows' VP8/VP9 decoders drop WebM's alpha channel; use "black = transparent" instead.
/// </summary>
public sealed class MediaFileSource : IDisposable
{
    private MediaPlayer? _player;
    private VideoFrame? _surface;
    private int _copying;

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Raised on a background thread. The handler owns and must Dispose the frame.</summary>
    public event EventHandler<CapturedFrame>? FrameArrived;

    public async Task StartAsync(string path)
    {
        Stop();

        var file = await StorageFile.GetFileFromPathAsync(path);
        var player = new MediaPlayer
        {
            IsLoopingEnabled = true,
            IsMuted = true,
            AutoPlay = false,
            IsVideoFrameServerEnabled = true,
        };

        var opened = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaOpened += (_, _) => opened.TrySetResult(null);
        player.MediaFailed += (_, e) => opened.TrySetResult($"{e.Error}: {e.ErrorMessage}");
        player.Source = MediaSource.CreateFromStorageFile(file);

        string? error;
        try
        {
            error = await opened.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            error = "o vídeo demorou demasiado a abrir";
        }

        int width = (int)player.PlaybackSession.NaturalVideoWidth, height = (int)player.PlaybackSession.NaturalVideoHeight;
        if (error is null && (width == 0 || height == 0)) error = "o ficheiro não tem imagem";
        if (error is not null)
        {
            player.Dispose();
            throw new InvalidOperationException(
                $"o Windows não consegue reproduzir este vídeo ({error}). Para WebM, instale as \"Extensões de Multimédia " +
                "Web\" e as \"Extensões de Vídeo VP9\" (gratuitas) da Microsoft Store");
        }

        _surface = VideoFrame.CreateAsDirect3D11SurfaceBacked(
            DirectXPixelFormat.B8G8R8A8UIntNormalized, width, height, WindowCaptureSource.Device);
        Width = width;
        Height = height;
        player.VideoFrameAvailable += OnVideoFrameAvailable;
        _player = player;
        player.Play();
    }

    public void Pause() => _player?.Pause();

    public void Resume() => _player?.Play();

    private void OnVideoFrameAvailable(MediaPlayer sender, object args)
    {
        // The GPU surface is reused: skip this frame if the previous copy is still running.
        if (_surface is not { } surface || Interlocked.Exchange(ref _copying, 1) == 1) return;
        try
        {
            sender.CopyFrameToVideoSurface(surface.Direct3DSurface);
        }
        catch
        {
            Volatile.Write(ref _copying, 0);
            return;
        }
        _ = CopyAsync(surface);
    }

    private async Task CopyAsync(VideoFrame surface)
    {
        try
        {
            var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(surface.Direct3DSurface, BitmapAlphaMode.Premultiplied);
            var handler = FrameArrived;
            if (handler is null) bitmap.Dispose();
            else handler(this, new CapturedFrame(bitmap, owner: null, MediaClock.Now));
        }
        catch
        {
            // Stopped meanwhile.
        }
        finally
        {
            Volatile.Write(ref _copying, 0);
        }
    }

    public void Stop()
    {
        if (_player is not null)
        {
            _player.VideoFrameAvailable -= OnVideoFrameAvailable;
            _player.Dispose();
            _player = null;
        }
        _surface?.Dispose();
        _surface = null;
    }

    public void Dispose() => Stop();
}

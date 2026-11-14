using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LCapture.Effects;
using LCapture.Interop;
using LCapture.Models;
using LCapture.Services;
using NAudio.CoreAudioApi;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using WinRT;

namespace LCapture;

public partial class MainWindow : Window
{
    private const string EnterFullscreenGlyph = "\uE740";
    private const string ExitFullscreenGlyph = "\uE73F";
    private const string VolumeGlyph = "\uE767";
    private const string MuteGlyph = "\uE74F";
    private const string CollapseGlyph = "\uE70E";
    private const string ExpandGlyph = "\uE70D";

    private readonly VideoCaptureService _capture = new();
    private readonly AudioLoopbackService _audio = new();
    private readonly SharpenEffect _sharpen = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _cursorIdleTimer;
    private readonly DispatcherTimer _saveTimer;

    private WriteableBitmap? _previewBitmap;
    private readonly object _pendingLock = new();
    private SoftwareBitmap? _pendingBitmap;
    private bool _renderQueued;

    private bool _suppressFormatEvent;
    private bool _suppressAudioEvent;
    private bool _suppressDeviceEvent;
    private bool _isStreaming;
    private bool _closeConfirmed;
    private bool _toolbarCollapsed;
    private string? _currentDeviceId;
    private string? _currentDeviceName;

    private bool _isFullscreen;
    private WindowState _prevState;
    private WindowStyle _prevStyle;
    private ResizeMode _prevResize;
    private Rect _prevBounds;

    public MainWindow()
    {
        InitializeComponent();

        _capture.FrameArrived += OnFrameArrived;
        _capture.ErrorOccurred += OnCaptureError;
        _audio.ErrorOccurred += OnAudioError;
        PreviewImage.SizeChanged += (_, _) => UpdateSharpenTexelSize();

        _cursorIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _cursorIdleTimer.Tick += (_, _) =>
        {
            _cursorIdleTimer.Stop();
            if (_isFullscreen) Cursor = Cursors.None;
        };

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _settings.Save();
        };

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadDevicesAsync();
        await LoadAudioDevicesAsync(keepCurrent: false);

        MuteButton.IsChecked = _settings.AudioMuted;
        UpdateMuteIcon();
        VolumeSlider.Value = _settings.AudioVolume;

        double strength = Math.Clamp(_settings.SharpenStrength, 0, 1);
        SharpenStrengthSlider.Value = strength;
        _sharpen.Strength = strength;
        SharpenValueText.Text = FormatPercent(strength);
        SharpenButton.IsChecked = _settings.SharpenEnabled;
        ApplySharpen();

        if (DevicesCombo.SelectedItem is DeviceInformation device)
            await StartCaptureAsync(device);
    }

    private async Task LoadDevicesAsync()
    {
        if (!_isStreaming)
            SetStatus("Procurando fontes de vídeo...");
        var previouslySelectedId = _currentDeviceId
                                   ?? (DevicesCombo.SelectedItem as DeviceInformation)?.Id
                                   ?? _settings.DeviceId;

        IReadOnlyList<DeviceInformation> devices;
        try
        {
            devices = await VideoCaptureService.GetDevicesAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Erro ao listar fontes de vídeo: {ex.Message}", StatusLevel.Error);
            return;
        }

        // Re-populating the list must not look like the user picked another source.
        _suppressDeviceEvent = true;
        try
        {
            DevicesCombo.ItemsSource = devices;
            DevicesCombo.SelectedItem = devices.Count == 0
                ? null
                : devices.FirstOrDefault(d => d.Id == previouslySelectedId) ?? VideoCaptureService.PickDefault(devices);
        }
        finally
        {
            _suppressDeviceEvent = false;
        }

        if (_isStreaming)
        {
            if (devices.Any(d => d.Id == _currentDeviceId))
                return; // Still connected: keep streaming and keep the current status.
            await StopCaptureAsync();
        }

        if (devices.Count == 0)
            SetStatus("Nenhuma fonte de vídeo encontrada. Conecte a placa de captura e clique em Atualizar.");
        else
            SetStatus($"{devices.Count} fonte(s) de vídeo encontrada(s). Selecione uma e clique em Iniciar.");
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async Task RefreshDevicesAsync()
    {
        await LoadDevicesAsync();
        await LoadAudioDevicesAsync(keepCurrent: true);
    }

    /// <summary>
    /// Fills the audio list and picks: the current choice (on refresh), the choice saved for this
    /// video source, the audio input of the same capture card, or nothing — never a random
    /// microphone, which would be played back through the speakers.
    /// </summary>
    private async Task LoadAudioDevicesAsync(bool keepCurrent)
    {
        var video = DevicesCombo.SelectedItem as DeviceInformation;
        var currentId = keepCurrent ? (AudioDevicesCombo.SelectedItem as MMDevice)?.ID : null;
        var savedId = video is not null && video.Id == _settings.DeviceId ? _settings.AudioDeviceId : null;

        IReadOnlyList<MMDevice> devices;
        MMDevice? sameHardware;
        try
        {
            devices = AudioLoopbackService.GetInputDevices();
            sameHardware = await AudioLoopbackService.FindForVideoSourceAsync(devices, video);
        }
        catch (Exception ex)
        {
            SetStatus($"Erro ao listar dispositivos de áudio: {ex.Message}", StatusLevel.Error);
            return;
        }

        _suppressAudioEvent = true;
        AudioDevicesCombo.ItemsSource = devices;
        AudioDevicesCombo.SelectedItem = devices.FirstOrDefault(d => d.ID == currentId)
                                         ?? devices.FirstOrDefault(d => d.ID == savedId)
                                         ?? sameHardware;
        _suppressAudioEvent = false;
    }

    private async void DevicesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeviceEvent) return;

        var device = DevicesCombo.SelectedItem as DeviceInformation;
        if (_isStreaming && device?.Id == _currentDeviceId) return;

        if (_isStreaming)
            await StopCaptureAsync();

        FormatsCombo.ItemsSource = null;
        await LoadAudioDevicesAsync(keepCurrent: false);
    }

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isStreaming)
        {
            await StopCaptureAsync();
            return;
        }

        if (DevicesCombo.SelectedItem is DeviceInformation device)
            await StartCaptureAsync(device);
        else
            SetStatus("Selecione uma fonte de vídeo primeiro.");
    }

    private async Task StartCaptureAsync(DeviceInformation device)
    {
        StartStopButton.IsEnabled = false;
        SetStatus($"Conectando a {device.Name}...");

        try
        {
            var allFormats = await _capture.OpenDeviceAsync(device.Id);
            if (allFormats.Count == 0)
            {
                SetStatus("Esta fonte não anunciou nenhum formato de vídeo suportado.");
                return;
            }

            var formats = CaptureFormatCatalog.Curate(allFormats);

            _suppressFormatEvent = true;
            FormatsCombo.ItemsSource = formats;

            // Reuse the last profile (resolution + fps) for this device; otherwise pick the best
            // one that fits the current screen.
            CaptureFormatOption? savedFormat = null;
            if (device.Id == _settings.DeviceId && _settings.FormatFpsDenominator > 0)
            {
                double savedFps = (double)_settings.FormatFpsNumerator / _settings.FormatFpsDenominator;
                savedFormat = formats.FirstOrDefault(f =>
                    f.SameProfile(_settings.FormatWidth, _settings.FormatHeight, savedFps));
            }

            var chosen = savedFormat ?? CaptureFormatCatalog.PickDefault(formats, ScreenHeightInPixels());
            FormatsCombo.SelectedItem = chosen;
            _suppressFormatEvent = false;

            await _capture.StartAsync(chosen);

            _currentDeviceId = device.Id;
            _currentDeviceName = device.Name;
            _isStreaming = true;
            StartStopButton.Content = "Parar";
            StartStopButton.Style = (Style)FindResource("DangerButtonStyle");
            NoSignalText.Visibility = Visibility.Collapsed;
            SetStatus($"Exibindo: {device.Name} — {chosen}", StatusLevel.Success);
            SaveSettings(device.Id, chosen);

            StartAudio();
        }
        catch (UnauthorizedAccessException)
        {
            SetStatus("Acesso negado à fonte de vídeo. Vá em Configurações do Windows > Privacidade e segurança > " +
                      "Câmera, e habilite \"Permitir que aplicativos de área de trabalho acessem sua câmera\".", StatusLevel.Error);
        }
        catch (Exception ex)
        {
            SetStatus($"Erro ao iniciar a captura: {ex.Message}", StatusLevel.Error);
        }
        finally
        {
            StartStopButton.IsEnabled = true;
        }
    }

    private double ScreenHeightInPixels() =>
        SystemParameters.PrimaryScreenHeight * System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleY;

    private async Task StopCaptureAsync()
    {
        StartStopButton.IsEnabled = false;
        try
        {
            await _capture.CloseAsync();
        }
        finally
        {
            _audio.Stop();
            _isStreaming = false;
            _currentDeviceId = null;
            StartStopButton.Content = "Iniciar";
            StartStopButton.Style = (Style)FindResource("AccentButtonStyle");
            StartStopButton.IsEnabled = true;
            NoSignalText.Visibility = Visibility.Visible;
            PreviewImage.Source = null;
            _previewBitmap = null;
            SetStatus("Captura parada.");
        }
    }

    private void StartAudio()
    {
        if (AudioDevicesCombo.SelectedItem is not MMDevice device)
        {
            SetStatus($"{StatusText.Text} · sem áudio (escolha a fonte de áudio)", StatusLevel.Success);
            return;
        }

        try
        {
            var muted = MuteButton.IsChecked == true;
            _audio.Start(device, muted ? 0f : (float)VolumeSlider.Value);
            SaveAudioSettings(device.ID);
        }
        catch (Exception ex)
        {
            SetStatus($"{StatusText.Text} (áudio indisponível: {ex.Message})", StatusLevel.Error);
        }
    }

    private void SaveAudioSettings(string deviceId)
    {
        _settings.AudioDeviceId = deviceId;
        _settings.AudioVolume = (float)VolumeSlider.Value;
        _settings.AudioMuted = MuteButton.IsChecked == true;
        _settings.Save();
    }

    private async void FormatsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFormatEvent || !_isStreaming) return;
        if (FormatsCombo.SelectedItem is not CaptureFormatOption format) return;

        SetStatus("Ajustando qualidade...");
        try
        {
            await _capture.StartAsync(format);
            SetStatus($"Exibindo: {_currentDeviceName} — {format}", StatusLevel.Success);
            if (DevicesCombo.SelectedItem is DeviceInformation device)
                SaveSettings(device.Id, format);
        }
        catch (Exception ex)
        {
            SetStatus($"Erro ao trocar a qualidade: {ex.Message}", StatusLevel.Error);
        }
    }

    private void AudioDevicesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAudioEvent || !_isStreaming) return;
        StartAudio();
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        bool muted = MuteButton.IsChecked == true;
        _audio.Volume = muted ? 0f : (float)VolumeSlider.Value;
        UpdateMuteIcon();
        _settings.AudioMuted = muted;
        _settings.Save();
    }

    private void UpdateMuteIcon() => MuteIcon.Text = MuteButton.IsChecked == true ? MuteGlyph : VolumeGlyph;

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MuteButton.IsChecked != true)
            _audio.Volume = (float)e.NewValue;

        // Debounced so dragging the slider does not rewrite the settings file continuously.
        _settings.AudioVolume = (float)e.NewValue;
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnAudioError(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() => SetStatus($"Erro no áudio: {message}", StatusLevel.Error));
    }

    private void SaveSettings(string deviceId, CaptureFormatOption format)
    {
        _settings.DeviceId = deviceId;
        _settings.FormatWidth = format.Width;
        _settings.FormatHeight = format.Height;
        _settings.FormatFpsNumerator = format.FrameRateNumerator;
        _settings.FormatFpsDenominator = format.FrameRateDenominator;
        _settings.FormatSubtype = format.Subtype;
        _settings.Save();
    }

    // ---- Frame rendering (background thread -> UI thread, dropping stale frames) ----

    private void OnFrameArrived(object? sender, SoftwareBitmap bitmap)
    {
        SoftwareBitmap? previous;
        bool needsSchedule;
        lock (_pendingLock)
        {
            previous = _pendingBitmap;
            _pendingBitmap = bitmap;
            needsSchedule = !_renderQueued;
            _renderQueued = true;
        }
        previous?.Dispose();

        if (needsSchedule)
            Dispatcher.BeginInvoke(DispatcherPriority.Render, RenderPendingFrame);
    }

    private void RenderPendingFrame()
    {
        SoftwareBitmap? bitmap;
        lock (_pendingLock)
        {
            bitmap = _pendingBitmap;
            _pendingBitmap = null;
            _renderQueued = false;
        }
        if (bitmap is null) return;

        try
        {
            UpdateWriteableBitmap(bitmap);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    private unsafe void UpdateWriteableBitmap(SoftwareBitmap bitmap)
    {
        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        if (width <= 0 || height <= 0) return;

        if (_previewBitmap is null || _previewBitmap.PixelWidth != width || _previewBitmap.PixelHeight != height)
        {
            _previewBitmap = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            PreviewImage.Source = _previewBitmap;
        }

        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        var access = reference.As<IMemoryBufferByteAccess>();
        access.GetBuffer(out byte* srcPtr, out uint capacity);

        var plane = buffer.GetPlaneDescription(0);

        _previewBitmap.Lock();
        try
        {
            byte* src = srcPtr + plane.StartIndex;
            byte* dst = (byte*)_previewBitmap.BackBuffer;
            int srcStride = plane.Stride;
            int dstStride = _previewBitmap.BackBufferStride;

            if (srcStride == dstStride)
            {
                Buffer.MemoryCopy(src, dst, (long)dstStride * height, (long)srcStride * height);
            }
            else
            {
                int rowBytes = Math.Min(srcStride, dstStride);
                for (int row = 0; row < height; row++)
                {
                    Buffer.MemoryCopy(src + (long)row * srcStride, dst + (long)row * dstStride, rowBytes, rowBytes);
                }
            }

            _previewBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            _previewBitmap.Unlock();
        }
    }

    private void OnCaptureError(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() => SetStatus($"Erro na fonte de vídeo: {message}", StatusLevel.Error));
    }

    private enum StatusLevel { Idle, Success, Error }

    private void SetStatus(string text, StatusLevel level = StatusLevel.Idle)
    {
        StatusText.Text = text;
        StatusDot.Fill = level switch
        {
            StatusLevel.Success => (System.Windows.Media.Brush)FindResource("SuccessBrush"),
            StatusLevel.Error => (System.Windows.Media.Brush)FindResource("DangerBrush"),
            _ => (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
        };
    }

    // ---- Fullscreen ----

    private void FullscreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    // ---- GPU sharpening ----

    private void SharpenButton_Changed(object sender, RoutedEventArgs e)
    {
        ApplySharpen();
        _settings.SharpenEnabled = SharpenButton.IsChecked == true;
        _settings.Save();
    }

    private void ApplySharpen()
    {
        bool on = SharpenButton.IsChecked == true;
        UpdateSharpenTexelSize();
        PreviewImage.Effect = on ? _sharpen : null;
        SharpenButton.ToolTip = on ? "Nitidez por GPU (ligada)" : "Nitidez por GPU (desligada)";
        SharpenStrengthPanel.IsEnabled = on;
        SharpenStrengthPanel.Opacity = on ? 1 : 0.4;
    }

    private void SharpenStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;

        _sharpen.Strength = e.NewValue;
        SharpenValueText.Text = FormatPercent(e.NewValue);

        _settings.SharpenStrength = e.NewValue;
        ScheduleSave();
    }

    private static string FormatPercent(double value) => $"{Math.Round(value * 100)}%";

    private void UpdateSharpenTexelSize()
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(PreviewImage);
        double w = PreviewImage.ActualWidth * dpi.DpiScaleX;
        double h = PreviewImage.ActualHeight * dpi.DpiScaleY;
        if (w >= 1 && h >= 1)
            _sharpen.TexelSize = new Point(1.0 / w, 1.0 / h);
    }

    // ---- Collapsible toolbar ----

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        _toolbarCollapsed = !_toolbarCollapsed;
        Toolbar.Visibility = _toolbarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseIcon.Text = _toolbarCollapsed ? ExpandGlyph : CollapseGlyph;
        CollapseButton.ToolTip = _toolbarCollapsed
            ? "Mostrar barra (ou passe o mouse no topo)"
            : "Ocultar barra";
    }

    private void ToolbarHoverZone_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_toolbarCollapsed)
            Toolbar.Visibility = Visibility.Visible;
    }

    private void Toolbar_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_toolbarCollapsed)
            Toolbar.Visibility = Visibility.Collapsed;
    }

    private void PreviewArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleFullscreen();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Escape when _isFullscreen:
                ExitFullscreen();
                e.Handled = true;
                break;
            case Key.F5 when !_isFullscreen:
                _ = RefreshDevicesAsync();
                e.Handled = true;
                break;
        }
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isFullscreen) return;
        Cursor = Cursors.Arrow;
        _cursorIdleTimer.Stop();
        _cursorIdleTimer.Start();
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen) ExitFullscreen();
        else EnterFullscreen();
    }

    private void FullscreenButtonResetIcon()
    {
        FullscreenIcon.Text = EnterFullscreenGlyph;
    }

    private void EnterFullscreen()
    {
        if (_isFullscreen) return;

        _prevState = WindowState;
        _prevStyle = WindowStyle;
        _prevResize = ResizeMode;
        _prevBounds = new Rect(Left, Top, Width, Height);

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        WindowState = WindowState.Maximized;

        _isFullscreen = true;
        FullscreenIcon.Text = ExitFullscreenGlyph;
        FullscreenButton.ToolTip = "Sair da tela cheia (Esc)";
        _cursorIdleTimer.Start();
    }

    private void ExitFullscreen()
    {
        if (!_isFullscreen) return;

        _cursorIdleTimer.Stop();
        Cursor = Cursors.Arrow;

        Topmost = false;
        WindowState = WindowState.Normal;
        WindowStyle = _prevStyle;
        ResizeMode = _prevResize;
        Left = _prevBounds.Left;
        Top = _prevBounds.Top;
        Width = _prevBounds.Width;
        Height = _prevBounds.Height;
        if (_prevState == WindowState.Maximized) WindowState = WindowState.Maximized;

        _isFullscreen = false;
        FullscreenButtonResetIcon();
        FullscreenButton.ToolTip = "Tela cheia (F11)";
    }

    // ---- Shutdown ----

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed) return;
        e.Cancel = true;

        _audio.Dispose();
        await _capture.DisposeAsync();
        _closeConfirmed = true;
        Close();
    }
}

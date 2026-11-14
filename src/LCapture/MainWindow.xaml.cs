using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LCapture.Effects;
using LCapture.Interop;
using LCapture.Models;
using LCapture.Services;
using NAudio.CoreAudioApi;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Devices;
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
    private CapturedFrame? _pendingFrame;
    private bool _renderQueued;

    private bool _suppressFormatEvent;
    private bool _suppressAudioEvent;
    private bool _suppressDeviceEvent;
    private bool _isStreaming;
    private bool _closeConfirmed;
    private bool _toolbarCollapsed;
    private string? _currentDeviceId;
    private string? _currentDeviceName;

    private readonly DispatcherTimer _deviceChangeTimer;
    private DeviceWatcher? _deviceWatcher;
    private volatile bool _deviceWatcherReady;
    // Set when the streaming source is unplugged; capture restarts when it reappears.
    private string? _reconnectDeviceId;

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

        _deviceChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _deviceChangeTimer.Tick += DeviceChangeTimer_Tick;

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

        // Tier 2 = full GPU acceleration. Below that (Remote Desktop, VMs, basic display driver)
        // WPF runs pixel shaders on the CPU, which would make the sharpen filter very slow.
        bool gpuEffects = RenderCapability.Tier >> 16 >= 2;
        if (gpuEffects)
            SharpenButton.IsChecked = _settings.SharpenEnabled;
        ApplySharpen();
        if (!gpuEffects)
        {
            SharpenButton.IsEnabled = false;
            ToolTipService.SetShowOnDisabled(SharpenButton, true);
            SharpenButton.ToolTip = "Nitidez indisponível: o Windows está a desenhar sem aceleração da placa de " +
                                    "vídeo (Área de Trabalho Remota, máquina virtual ou driver genérico).";
        }

        if (SelectedVideoDevice is DeviceInformation device)
            await StartCaptureAsync(device);

        StartDeviceWatcher();
    }

    // ---- Hot-plug: refresh the lists and reconnect when the capture card comes back ----

    private void StartDeviceWatcher()
    {
        var watcher = DeviceInformation.CreateWatcher(MediaDevice.GetVideoCaptureSelector());
        // Added fires once per existing device before EnumerationCompleted; those are not changes.
        watcher.EnumerationCompleted += (_, _) => _deviceWatcherReady = true;
        watcher.Added += (_, _) => { if (_deviceWatcherReady) OnDevicesChanged(); };
        watcher.Removed += (_, _) => OnDevicesChanged();
        watcher.Updated += (_, _) => { }; // Required for Removed to be raised.
        watcher.Start();
        _deviceWatcher = watcher;
    }

    // Plugging in a card raises several events in a burst; refresh once it settles.
    private void OnDevicesChanged() => Dispatcher.BeginInvoke(() =>
    {
        _deviceChangeTimer.Stop();
        _deviceChangeTimer.Start();
    });

    private async void DeviceChangeTimer_Tick(object? sender, EventArgs e)
    {
        _deviceChangeTimer.Stop();
        await RefreshDevicesAsync();

        if (!_isStreaming && _reconnectDeviceId is not null &&
            SelectedVideoDevice is DeviceInformation device && device.Id == _reconnectDeviceId)
        {
            await StartCaptureAsync(device);
        }
    }

    private DeviceInformation? SelectedVideoDevice => (DevicesCombo.SelectedItem as VideoSourceItem)?.Device;

    /// <summary>
    /// List item for a video source. WinRT DeviceInformation is a COM object, and WPF reads
    /// properties of COM objects through a WinForms type descriptor that the self-contained
    /// (WPF-only) build does not ship, so the combo must never bind to it directly.
    /// </summary>
    private sealed record VideoSourceItem(DeviceInformation Device)
    {
        public string Name => Device.Name;
        public override string ToString() => Name;
    }

    private async Task LoadDevicesAsync()
    {
        if (!_isStreaming)
            SetStatus("Procurando fontes de vídeo...");
        var previouslySelectedId = _currentDeviceId
                                   ?? _reconnectDeviceId
                                   ?? SelectedVideoDevice?.Id
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
            var items = devices.Select(d => new VideoSourceItem(d)).ToList();
            var chosen = devices.Count == 0
                ? null
                : devices.FirstOrDefault(d => d.Id == previouslySelectedId) ?? VideoCaptureService.PickDefault(devices);
            DevicesCombo.ItemsSource = items;
            DevicesCombo.SelectedItem = items.FirstOrDefault(i => i.Device == chosen);
        }
        finally
        {
            _suppressDeviceEvent = false;
        }

        if (_isStreaming)
        {
            if (devices.Any(d => d.Id == _currentDeviceId))
                return; // Still connected: keep streaming and keep the current status.

            _reconnectDeviceId = _currentDeviceId;
            await StopCaptureAsync();
            SetStatus("A fonte de vídeo foi desligada. A captura recomeça sozinha quando voltar a ser ligada.",
                      StatusLevel.Error);
            return;
        }

        if (_reconnectDeviceId is not null && devices.All(d => d.Id != _reconnectDeviceId))
            return; // Still waiting for the unplugged source; keep that message.

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
        var video = SelectedVideoDevice;
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

        var device = SelectedVideoDevice;
        if (_isStreaming && device?.Id == _currentDeviceId) return;

        _reconnectDeviceId = null; // The user picked something else; stop waiting for the old source.
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

        if (SelectedVideoDevice is DeviceInformation device)
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
            _reconnectDeviceId = null;
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
        MonitorInfo.GetHeightInPixels(new WindowInteropHelper(this).Handle)
        ?? SystemParameters.PrimaryScreenHeight * System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleY;

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
            if (SelectedVideoDevice is DeviceInformation device)
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
        if (!IsLoaded) return; // Raised while the XAML is still being built.

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

    private void OnFrameArrived(object? sender, CapturedFrame frame)
    {
        CapturedFrame? previous;
        bool needsSchedule;
        lock (_pendingLock)
        {
            previous = _pendingFrame;
            _pendingFrame = frame;
            needsSchedule = !_renderQueued;
            _renderQueued = true;
        }
        previous?.Dispose();

        if (needsSchedule)
            Dispatcher.BeginInvoke(DispatcherPriority.Render, RenderPendingFrame);
    }

    private void RenderPendingFrame()
    {
        CapturedFrame? frame;
        lock (_pendingLock)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
            _renderQueued = false;
        }
        if (frame is null) return;

        try
        {
            UpdateWriteableBitmap(frame.Bitmap);
        }
        finally
        {
            frame.Dispose();
        }
    }

    private unsafe void UpdateWriteableBitmap(SoftwareBitmap bitmap)
    {
        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        if (width <= 0 || height <= 0) return;

        if (_previewBitmap is null || _previewBitmap.PixelWidth != width || _previewBitmap.PixelHeight != height)
        {
            // Bgr32: same memory layout as Bgra8 but WPF ignores the alpha byte, which capture
            // pipelines do not always set to opaque.
            _previewBitmap = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
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
        CollapseIcon.Text = _toolbarCollapsed ? ExpandGlyph : CollapseGlyph;
        CollapseButton.ToolTip = _toolbarCollapsed
            ? "Mostrar barra (ou passe o mouse no topo)"
            : "Ocultar barra";
        UpdateToolbarLayout();
    }

    /// <summary>In fullscreen or when collapsed, the toolbar hides and floats over the video on hover.</summary>
    private bool ToolbarAutoHides => _toolbarCollapsed || _isFullscreen;

    /// <summary>
    /// Docked toolbar: the video sits below it, so nothing of the picture is covered. Auto-hide:
    /// the video fills the window and the toolbar appears on top of it only while hovered.
    /// </summary>
    private void UpdateToolbarLayout()
    {
        bool overlay = ToolbarAutoHides;
        Grid.SetRow(PreviewArea, overlay ? 0 : 1);
        Grid.SetRowSpan(PreviewArea, overlay ? 2 : 1);
        Toolbar.Visibility = overlay ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ToolbarHoverZone_MouseEnter(object sender, MouseEventArgs e)
    {
        if (ToolbarAutoHides)
            Toolbar.Visibility = Visibility.Visible;
    }

    private void Toolbar_MouseLeave(object sender, MouseEventArgs e)
    {
        // An open drop-down lives in its own popup window, so the mouse "leaves" the toolbar.
        bool dropDownOpen = DevicesCombo.IsDropDownOpen || FormatsCombo.IsDropDownOpen ||
                            AudioDevicesCombo.IsDropDownOpen;
        if (ToolbarAutoHides && !dropDownOpen)
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
        UpdateToolbarLayout();
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
        UpdateToolbarLayout();
        FullscreenButtonResetIcon();
        FullscreenButton.ToolTip = "Tela cheia (F11)";
    }

    // ---- Shutdown ----

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed) return;
        e.Cancel = true;

        _deviceChangeTimer.Stop();
        _deviceWatcher?.Stop();
        _audio.Dispose();
        await _capture.DisposeAsync();
        _closeConfirmed = true;
        Close();
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    private readonly DispatcherTimer _recordTimer;

    // Read on the capture threads (video and audio), written on the UI thread.
    private RecordingService? _recorder;
    private MixerInput? _cardInput;
    private MixerInput? _windowInput;
    private readonly DispatcherTimer _layerTimer;
    private CaptureFormatOption? _currentFormat;
    private bool _suppressRecordingOptionEvent;
    private int _recordTicks;

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

        // The version in the title tells this build apart from an older installed copy.
        var version = typeof(MainWindow).Assembly.GetName().Version;
        Title = $"LCapture {version?.Major}.{version?.Minor}.{version?.Build} — Captura de Vídeo";

        _capture.FrameArrived += (_, frame) => OnSourceFrame(DeviceKey, frame);
        _capture.ErrorOccurred += OnCaptureError;
        _audio.ErrorOccurred += OnAudioError;
        _audio.DataCaptured += (buffer, count, _) => Volatile.Read(ref _cardInput)?.Write(buffer, count);
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

        _recordTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _recordTimer.Tick += RecordTimer_Tick;

        _layerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        _layerTimer.Tick += (_, _) => CheckWindowState();

        _deviceChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _deviceChangeTimer.Tick += DeviceChangeTimer_Tick;

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao abrir a janela", ex);
            SetStatus($"Erro ao iniciar: {ex.Message}", StatusLevel.Error);
        }
    }

    private async Task LoadAsync()
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

        Log.Info("A preparar camadas, áudio e gravação");
        InitLayers();
        InitAudioPanel();
        InitRecordingOptions();
        UpdateToolbarLayout();
        _layerTimer.Start();
        Log.Info($"Pronto; fonte escolhida: {SelectedVideoDevice?.Name ?? "nenhuma"}");

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
        Log.Info("Dispositivos de vídeo mudaram; a atualizar a lista");
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
        if (!_isStreaming && _statusLevel != StatusLevel.Error)
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
            var saved = await StopCaptureAsync();
            SetStatus("A fonte de vídeo foi desligada. A captura recomeça sozinha quando voltar a ser ligada." +
                      (saved is null ? "" : $" {saved}"), StatusLevel.Error);
            return;
        }

        if (_reconnectDeviceId is not null && devices.All(d => d.Id != _reconnectDeviceId))
            return; // Still waiting for the unplugged source; keep that message.

        if (devices.Count == 0)
            SetStatus("Nenhuma fonte de vídeo encontrada. Conecte a placa de captura e clique em Atualizar.");
        else if (_statusLevel != StatusLevel.Error || StatusText.Text.StartsWith("Procurando"))
            SetStatus($"{devices.Count} fonte(s) de vídeo encontrada(s). Selecione uma e clique em Iniciar.");
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async Task RefreshDevicesAsync()
    {
        await LoadDevicesAsync();
        await LoadAudioDevicesAsync(keepCurrent: true);
        LoadMicDevices();

        var present = (DevicesCombo.ItemsSource as IEnumerable<VideoSourceItem>)?.Select(d => d.Device.Id).ToHashSet()
                      ?? [];
        await ReconcileCamerasAsync(present);
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
        if (device?.Id is string id)
            await ReleaseCameraForDeviceAsync(id); // It cannot be opened twice.
        if (_isStreaming)
            await StopCaptureAsync();

        FormatsCombo.ItemsSource = null;
        await LoadAudioDevicesAsync(keepCurrent: false);
        UpdateLayerControls();
    }

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isStreaming)
        {
            // Easy to mistake for "stop recording": ask before ending a recording this way.
            if (IsRecording && MainLayer == LayerSource.Device &&
                MessageBox.Show(this,
                    "Está a gravar. Desligar a fonte de vídeo também termina a gravação.\n\n" +
                    "Para parar só a gravação, use o botão vermelho ■ (ou F9).\n\nDesligar a fonte mesmo assim?",
                    "LCapture", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }
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
        // A device can only be opened once: if it is the overlay's webcam, take it off the corner.
        await ReleaseCameraForDeviceAsync(device.Id);

        StartStopButton.IsEnabled = false;
        SetStatus($"Conectando a {device.Name}...");

        try
        {
            Log.Info($"A abrir {device.Name} ({device.Id})");
            var allFormats = await _capture.OpenDeviceAsync(device.Id);
            Log.Info($"{allFormats.Count} formatos anunciados");
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

            Log.Info($"A iniciar o formato {chosen} ({chosen.Subtype})");
            await _capture.StartAsync(chosen);
            Log.Info("Leitor de quadros a funcionar");

            _currentFormat = chosen;
            UpdateRecordingOptions();
            _currentDeviceId = device.Id;
            _currentDeviceName = device.Name;
            _reconnectDeviceId = null;
            _isStreaming = true;
            // Neutral, not red: the only red button stops the recording, not the source.
            StartStopText.Text = "Desligar";
            StartStopButton.ToolTip = "Desligar a fonte de vídeo (termina também uma gravação)";
            StartStopButton.Style = (Style)FindResource(typeof(Button));
            UpdateRecordUi();
            UpdateLayerControls();
            if (MainLayer == LayerSource.Device)
                NoSignalText.Visibility = Visibility.Collapsed;
            SetStatus($"Exibindo: {device.Name} — {chosen}", StatusLevel.Success);
            SaveSettings(device.Id, chosen);

            StartAudio();
        }
        catch (UnauthorizedAccessException)
        {
            SetStatus("Acesso negado à fonte de vídeo. Vá em Configurações do Windows > Privacidade e segurança > " +
                      "Câmera e permita o acesso ao LCapture (ou \"Permitir que aplicativos de área de trabalho " +
                      "acessem sua câmera\", se não o instalou pela Microsoft Store).", StatusLevel.Error);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070490) && Environment.ProcessPath?.StartsWith(@"\\") == true)
        {
            // Seen when running from \\wsl.localhost\...: Windows' camera privacy does not handle
            // apps on network paths and the device then looks "not found".
            Log.Error("Erro ao iniciar a captura (caminho de rede)", ex);
            SetStatus("Erro ao abrir a placa: o LCapture está a correr de uma pasta de rede " +
                      $"({System.IO.Path.GetDirectoryName(Environment.ProcessPath)}), e o Windows não deixa usar " +
                      "a câmera assim. Copie-o para uma pasta do PC e abra-o de lá.", StatusLevel.Error);
        }
        catch (Exception ex)
        {
            Log.Error("Erro ao iniciar a captura", ex);
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

    /// <summary>Stops the toolbar's video source. Returns the recording's status line if it ended one.</summary>
    private async Task<string?> StopCaptureAsync()
    {
        StartStopButton.IsEnabled = false;
        // The recording follows the main picture; it only ends if this source was it.
        var saved = MainLayer == LayerSource.Device ? await StopRecordingAsync() : null;
        try
        {
            await _capture.CloseAsync();
        }
        finally
        {
            _audio.Stop();
            DetachCardAudio();
            _isStreaming = false;
            _currentDeviceId = null;
            _currentFormat = null;
            UpdateRecordingOptions();
            UpdateRecordUi();
            StartStopText.Text = "Iniciar";
            StartStopButton.ToolTip = "Ligar a fonte de vídeo";
            StartStopButton.Style = (Style)FindResource("AccentButtonStyle");
            StartStopButton.IsEnabled = true;
            if (MainLayer == LayerSource.Device)
                ClearPreview();
            ClearDeviceLayerContent();
            SetStatus(saved is null ? "Captura parada." : $"Captura parada. {saved}");
        }

        return saved;
    }

    private void ClearPreview()
    {
        lock (_pendingLock)
        {
            _pendingFrame?.Dispose();
            _pendingFrame = null;
        }
        NoSignalText.Visibility = Visibility.Visible;
        PreviewImage.Source = null;
        _previewBitmap = null;
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
            AttachCardAudio();
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

        // A recording has a fixed size and frame rate; it ends before the source changes format.
        var saved = MainLayer == LayerSource.Device ? await StopRecordingAsync() : null;
        SetStatus("Ajustando qualidade...");
        try
        {
            await _capture.StartAsync(format);
            _currentFormat = format;
            UpdateRecordingOptions();
            SetStatus($"Exibindo: {_currentDeviceName} — {format}" + (saved is null ? "" : $" · {saved}"),
                      StatusLevel.Success);
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

        // A recording keeps going: the new device joins its audio mix (see AttachCardAudio).
        StartAudio();
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        bool muted = MuteButton.IsChecked == true;
        _audio.Volume = muted ? 0f : (float)VolumeSlider.Value;
        UpdateCardGain();
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
        UpdateCardGain();
        UpdateVolumeTexts();

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

    /// <summary>A frame of the main layer (capture thread): add the overlay, record, then show.</summary>
    private void OnMainFrame(CapturedFrame source)
    {
        CapturedFrame frame;
        try
        {
            frame = _compositor.Compose(source);
        }
        catch
        {
            source.Dispose();
            return;
        }

        // The recorder copies the pixels now, before the preview takes ownership of the frame.
        Volatile.Read(ref _recorder)?.AddFrame(frame);

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
            NoSignalText.Visibility = Visibility.Collapsed;
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
            Dispatcher.BeginInvoke(UpdateSelectionAdorner, DispatcherPriority.Loaded);
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

    private StatusLevel _statusLevel;

    private void SetStatus(string text, StatusLevel level = StatusLevel.Idle)
    {
        if (level == StatusLevel.Error) Log.Error(text);
        else Log.Info(text);
        StatusText.Text = text;
        _statusLevel = level;
        StatusDot.Fill = level switch
        {
            StatusLevel.Success => (System.Windows.Media.Brush)FindResource("SuccessBrush"),
            StatusLevel.Error => (System.Windows.Media.Brush)FindResource("DangerBrush"),
            _ => (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
        };
    }

    // ---- Recording ----

    private const long MinFreeBytesToStart = 2L * 1024 * 1024 * 1024;
    private const long MinFreeBytesWhileRecording = 500L * 1024 * 1024;

    private sealed record ResolutionItem(int Height, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record FpsItem(bool Fps30, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record QualityItem(RecordingQuality Quality, string Label)
    {
        public override string ToString() => Label;
    }

    private bool IsRecording => _recorder is not null;

    private string RecordingFolder => string.IsNullOrWhiteSpace(_settings.RecordingFolder)
        ? AppSettings.DefaultRecordingFolder
        : _settings.RecordingFolder;

    private RecordingChoice CurrentRecordingChoice => new(
        (RecResolutionCombo.SelectedItem as ResolutionItem)?.Height ?? 0,
        (RecFpsCombo.SelectedItem as FpsItem)?.Fps30 ?? false,
        (RecQualityCombo.SelectedItem as QualityItem)?.Quality ?? RecordingQuality.High);

    private void InitRecordingOptions()
    {
        _suppressRecordingOptionEvent = true;
        var qualities = new List<QualityItem>
        {
            new(RecordingQuality.High, "Alta"),
            new(RecordingQuality.Medium, "Média"),
            new(RecordingQuality.Low, "Baixa (ficheiros mais pequenos)"),
        };
        RecQualityCombo.ItemsSource = qualities;
        RecQualityCombo.SelectedItem = qualities.FirstOrDefault(q => q.Quality == _settings.RecordingQuality) ?? qualities[0];
        _suppressRecordingOptionEvent = false;

        UpdateRecordingOptions();
        UpdateRecordUi();
    }

    /// <summary>Rebuilds the resolution/fps lists for the current source and refreshes the estimate.</summary>
    private void UpdateRecordingOptions()
    {
        _suppressRecordingOptionEvent = true;
        var source = MainFormat;

        var resolutions = new List<ResolutionItem>
        {
            new(0, source is null ? "Igual à fonte" : $"Igual à fonte ({source.Width}×{source.Height})"),
        };
        foreach (int height in RecordingProfile.PresetHeights)
        {
            if (source is not null && height >= source.Height) continue;
            var label = $"{height}p";
            if (source is not null)
            {
                var (w, h) = RecordingProfile.OutputSize(source.Width, source.Height, height);
                label += $" ({w}×{h})";
            }
            resolutions.Add(new ResolutionItem(height, label));
        }
        RecResolutionCombo.ItemsSource = resolutions;
        RecResolutionCombo.SelectedItem = resolutions.FirstOrDefault(r => r.Height == _settings.RecordingHeight)
                                          ?? resolutions[0];

        var rates = new List<FpsItem>
        {
            new(false, source is null ? "Igual à fonte" : $"Igual à fonte ({source.Fps:0.##} fps)"),
            new(true, "30 fps (ficheiros mais pequenos)"),
        };
        RecFpsCombo.ItemsSource = rates;
        RecFpsCombo.SelectedItem = rates[_settings.RecordingFps30 ? 1 : 0];

        _suppressRecordingOptionEvent = false;
        UpdateRecordingEstimate();
    }

    private void UpdateRecordingEstimate()
    {
        RecFolderText.Text = RecordingFolder;
        RecFolderText.ToolTip = RecordingFolder;

        if (MainFormat is not VideoFormatInfo source)
        {
            RecEstimateText.Text = "Inicie a captura para ver o tamanho estimado.";
            return;
        }

        var profile = RecordingProfile.Resolve(source, CurrentRecordingChoice);
        RecEstimateText.Text = $"{profile.Width}×{profile.Height} a {profile.Fps:0.##} fps · " +
                               $"{profile.VideoBitrate / 1e6:0.#} Mbps · cerca de {profile.GigabytesPerHour:0.#} GB por hora";
    }

    private void RecordingOption_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRecordingOptionEvent || !IsLoaded) return;

        var choice = CurrentRecordingChoice;
        _settings.RecordingHeight = choice.TargetHeight;
        _settings.RecordingFps30 = choice.Fps30;
        _settings.RecordingQuality = choice.Quality;
        _settings.Save();
        UpdateRecordingEstimate();
    }

    private void RecordSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateRecordingOptions();
        RecordSettingsPopup.IsOpen = !RecordSettingsPopup.IsOpen;
    }

    private void ChangeFolderButton_Click(object sender, RoutedEventArgs e)
    {
        RecordSettingsPopup.IsOpen = false;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Pasta para as gravações",
            InitialDirectory = Directory.Exists(RecordingFolder) ? RecordingFolder : "",
        };
        if (dialog.ShowDialog(this) != true) return;

        _settings.RecordingFolder = dialog.FolderName;
        _settings.Save();
        UpdateRecordingEstimate();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        RecordSettingsPopup.IsOpen = false;
        try
        {
            Directory.CreateDirectory(RecordingFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{RecordingFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Não foi possível abrir a pasta: {ex.Message}", StatusLevel.Error);
        }
    }

    private async void RecordButton_Click(object sender, RoutedEventArgs e) => await ToggleRecordingAsync();

    private async Task ToggleRecordingAsync()
    {
        if (!RecordButton.IsEnabled) return; // Not streaming, or busy starting/finishing a file.

        if (IsRecording)
        {
            var saved = await StopRecordingAsync();
            if (saved is not null) SetStatus(saved, StatusLevel.Success);
            return;
        }

        if (!MainActive || MainFormat is not VideoFormatInfo source)
        {
            SetStatus(MainLayer == LayerSource.Window
                ? "A janela ainda não enviou imagem. Mexa na janela ou espere um instante e tente de novo."
                : "Inicie a captura antes de gravar.");
            return;
        }

        var folder = RecordingFolder;
        var free = FreeBytes(folder);
        if (free is < MinFreeBytesToStart)
        {
            SetStatus($"Pouco espaço livre no disco da pasta de gravações ({free / 1e9:0.#} GB). " +
                      "Liberte espaço ou escolha outra pasta nas definições da gravação.", StatusLevel.Error);
            return;
        }

        var profile = RecordingProfile.Resolve(source, CurrentRecordingChoice);
        var path = Path.Combine(folder, $"LCapture {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");

        RecordButton.IsEnabled = false;
        RecordSettingsPopup.IsOpen = false;
        try
        {
            var recorder = await RecordingService.StartAsync(path, source, profile);
            recorder.Failed += (_, message) => Dispatcher.BeginInvoke(async () =>
            {
                if (_recorder != recorder) return;
                await StopRecordingAsync();
                SetStatus($"A gravação parou: {message}", StatusLevel.Error);
            });
            // Every frame from now on has the recording's size, whatever the layers do.
            _compositor.LockedCanvas = ((int)source.Width, (int)source.Height);
            Volatile.Write(ref _recorder, recorder);
            AttachCardAudio();
            UpdateMicState();
            await UpdateWindowAudioAsync();
            ReplayWindowFrame(); // A still window main picture: the recording starts with it.
            _recordTicks = 0;
            _recordTimer.Start();

            var notes = new List<string>();
            if (_cardInput is null && _windowInput is null && _micInput is null)
                notes.Add("sem som (escolha a fonte de áudio no painel Áudio)");
            else if (_micInput is not null)
                notes.Add("com microfone");
            if (MediaFoundationInterop.HasHardwareH264Encoder() == false)
                notes.Add("sem codificador de hardware: a gravação usa o processador e pode perder quadros");
            else if (source.Height >= 2160 && source.Fps > 31 && _compositor.HasLayers)
                notes.Add("4K60 com sobreposição é pesado; se perder quadros, grave em 1080p");

            SetStatus($"A gravar {Path.GetFileName(path)} — {profile.Width}×{profile.Height}, {profile.Fps:0.##} fps" +
                      string.Concat(notes.Select(n => " · " + n)), StatusLevel.Success);
        }
        catch (UnauthorizedAccessException)
        {
            SetStatus($"Sem permissão para gravar em {folder}. Escolha outra pasta nas definições da gravação.",
                      StatusLevel.Error);
        }
        catch (Exception ex)
        {
            SetStatus($"Não foi possível começar a gravar: {ex.Message}", StatusLevel.Error);
        }
        finally
        {
            UpdateRecordUi();
        }
    }

    /// <summary>Finishes the file. Returns a status line describing it, or null if nothing was recording.</summary>
    private async Task<string?> StopRecordingAsync()
    {
        var recorder = _recorder;
        if (recorder is null) return null;

        Volatile.Write(ref _recorder, null);
        _recordTimer.Stop();
        var duration = recorder.Elapsed;
        DetachCardAudio();
        DetachMicAudio();
        UpdateMicState(); // Closes the microphone unless the audio panel still shows its meter.
        await UpdateWindowAudioAsync(); // Stops listening to the window's app.
        _compositor.LockedCanvas = null;

        RecordButton.IsEnabled = false;
        RecordText.Text = "A guardar…";
        try
        {
            await recorder.StopAsync();
            var message = $"Gravação guardada: {Path.GetFileName(recorder.FilePath)} ({FormatDuration(duration)}).";
            if (recorder.FramesDropped > 0)
                message += $" {recorder.FramesDropped} quadros perdidos: o PC não acompanhou a codificação " +
                           "(experimente uma resolução ou taxa de quadros mais baixa).";
            return message;
        }
        catch (Exception ex)
        {
            return $"Erro ao terminar a gravação ({Path.GetFileName(recorder.FilePath)}): {ex.Message}";
        }
        finally
        {
            UpdateRecordUi();
        }
    }

    private void RecordTimer_Tick(object? sender, EventArgs e)
    {
        if (_recorder is not RecordingService recorder) return;

        RecordText.Text = FormatDuration(recorder.Elapsed);

        // Every ~15 s: stop cleanly before the disk fills up, which would leave a broken file.
        if (++_recordTicks % 30 == 0 && FreeBytes(RecordingFolder) is < MinFreeBytesWhileRecording)
            _ = StopForLowDiskAsync();
    }

    private async Task StopForLowDiskAsync()
    {
        var saved = await StopRecordingAsync();
        SetStatus($"Disco quase cheio: a gravação foi terminada. {saved}", StatusLevel.Error);
    }

    private void UpdateRecordUi()
    {
        bool recording = IsRecording;
        RecordButton.IsEnabled = recording || MainActive;
        RecordButton.Style = recording ? (Style)FindResource("DangerButtonStyle") : (Style)FindResource(typeof(Button));
        RecordButton.ToolTip = recording ? "Parar a gravação (F9)" : "Gravar (F9)";
        // Red dot = "record"; white square = "stop recording".
        RecordDot.Background = recording
            ? (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
            : (System.Windows.Media.Brush)FindResource("DangerBrush");
        RecordDot.CornerRadius = new CornerRadius(recording ? 2 : 5);
        if (!recording) RecordText.Text = "Gravar";

        RecResolutionCombo.IsEnabled = !recording;
        RecFpsCombo.IsEnabled = !recording;
        RecQualityCombo.IsEnabled = !recording;
        ChangeFolderButton.IsEnabled = !recording;
    }

    // ---- Recording audio: each source is one input of the recording's mixer ----

    private void AttachCardAudio()
    {
        DetachCardAudio();
        if (_recorder is not RecordingService recorder || _audio.CaptureFormat is not { } format) return;

        var input = recorder.AddAudioInput(format);
        if (input is not null)
            input.Gain = MuteButton.IsChecked == true ? 0f : (float)VolumeSlider.Value;
        Volatile.Write(ref _cardInput, input);
    }

    private void DetachCardAudio()
    {
        var input = Interlocked.Exchange(ref _cardInput, null);
        _recorder?.RemoveAudioInput(input);
    }

    /// <summary>The recording hears the card at the volume you hear it (muted = silent).</summary>
    private void UpdateCardGain()
    {
        if (_cardInput is MixerInput input)
            input.Gain = MuteButton.IsChecked == true ? 0f : (float)VolumeSlider.Value;
    }

    private void AttachWindowAudio()
    {
        DetachWindowAudio();
        if (_recorder is not RecordingService recorder) return;

        var input = recorder.AddAudioInput(ProcessAudioCapture.CaptureFormat);
        if (input is not null) input.Gain = WindowAudioGain;
        Volatile.Write(ref _windowInput, input);
    }

    private void DetachWindowAudio()
    {
        var input = Interlocked.Exchange(ref _windowInput, null);
        _recorder?.RemoveAudioInput(input);
    }

    private static long? FreeBytes(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return null; // Network paths and the like: don't block recording on a failed check.
        }
    }

    private static string FormatDuration(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");

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
            ? "Mostrar as barras (ou passe o rato no topo)"
            : "Ocultar barras";
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
        Grid.SetRowSpan(PreviewArea, overlay ? 3 : 1);
        Grid.SetColumnSpan(PreviewArea, overlay ? 2 : 1);
        Toolbar.Visibility = overlay ? Visibility.Collapsed : Visibility.Visible;
        StatusBar.Visibility = overlay ? Visibility.Collapsed : Visibility.Visible;
        AudioPanel.Visibility = !overlay && AudioPanelButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateMicState(); // The microphone is open only while its meter is visible or it is being recorded.
        UpdateSelectionAdorner();
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
                            AudioDevicesCombo.IsDropDownOpen || RecordSettingsPopup.IsOpen ||
                            LayersPopup.IsOpen;
        if (ToolbarAutoHides && !dropDownOpen)
            Toolbar.Visibility = Visibility.Collapsed;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Delete or Key.Escape when HandleLayerKey(e.Key):
                e.Handled = true;
                break;
            case Key.Escape when _isFullscreen:
                ExitFullscreen();
                e.Handled = true;
                break;
            case Key.F9:
                _ = ToggleRecordingAsync();
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
        _layerTimer.Stop();
        if (_saveTimer.IsEnabled)
        {
            // A layer change debounces the save by 400ms; closing right after one must not lose it.
            _saveTimer.Stop();
            _settings.Save();
        }
        await StopRecordingAsync(); // Closing mid-recording must still leave a playable file.
        _meterTimer?.Stop();
        _mic.Dispose();
        await StopLayersAsync();
        _audio.Dispose();
        await _capture.DisposeAsync();
        _closeConfirmed = true;
        Close();
    }
}

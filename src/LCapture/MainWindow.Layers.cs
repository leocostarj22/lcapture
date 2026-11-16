using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using LCapture.Interop;
using LCapture.Models;
using LCapture.Services;
using Windows.Graphics.Imaging;

namespace LCapture;

/// <summary>Where the main picture comes from.</summary>
public enum LayerSource
{
    /// <summary>The video source chosen in the toolbar (normally the capture card).</summary>
    Device,
    /// <summary>A window or a whole monitor.</summary>
    Window,
}

/// <summary>
/// The scene: a main picture (the toolbar's video source, or a window/screen) and any number of
/// layers over it — webcams, pictures (logos, frames), the window, or the capture card — each
/// with a free position and size. Every source's frames are routed by the role it has right now,
/// so changing the layers needs no restart.
/// </summary>
public partial class MainWindow
{
    private const string DeviceKey = "device";
    private const string WindowKey = "window";

    private readonly FrameCompositor _compositor = new();
    private readonly WindowCaptureSource _window = new();
    private readonly Dictionary<string, VideoCaptureService> _cameras = [];
    private readonly Dictionary<Guid, MediaFileSource> _media = [];
    private readonly ProcessAudioCapture _windowAudio = new();

    /// <summary>Layers over the main picture, bottom to top (UI thread).</summary>
    private readonly List<SceneLayer> _layers = [];
    private SceneLayer? _selectedLayer;

    // Read on capture threads.
    private volatile int _mainLayer = (int)LayerSource.Device;
    private volatile Dictionary<string, Guid> _routes = [];
    private VideoFormatInfo? _windowFormat;

    private CaptureTarget? _windowTarget;
    private (int Pid, bool Exclude)? _windowAudioTarget;
    private bool _minimizedWarned;
    private bool _suppressLayerEvent;

    private LayerSource MainLayer => (LayerSource)_mainLayer;
    private string MainKey => MainLayer == LayerSource.Window ? WindowKey : DeviceKey;

    private sealed record LayerItem(LayerSource Kind, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record AddItem(string Action, string? DeviceId, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record AttachItem(Guid? Target, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record CaptureMethodItem(CaptureMethod Method, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>Whether the main layer is producing pictures (needed to record).</summary>
    private bool MainActive => MainLayer == LayerSource.Window ? _window.IsRunning : _isStreaming;

    /// <summary>Size and rate of the main layer's frames, or null if not known yet.</summary>
    private VideoFormatInfo? MainFormat => MainLayer == LayerSource.Window
        ? _windowFormat
        : _currentFormat is null ? null : VideoFormatInfo.From(_currentFormat);

    /// <summary>Width/height of the picture the layers are placed on.</summary>
    private double CanvasAspect
    {
        get
        {
            if (_compositor.LockedCanvas is { } locked) return (double)locked.Width / locked.Height;
            if (MainFormat is { Height: > 0 } f) return (double)f.Width / f.Height;
            return 16.0 / 9.0;
        }
    }

    private bool LayersLocked => LockLayersButton.IsChecked == true;

    private void InitLayers()
    {
        _window.FrameArrived += (_, frame) => OnSourceFrame(WindowKey, frame);
        _window.Closed += (_, _) => Dispatcher.BeginInvoke(OnWindowClosed);
        _windowAudio.DataCaptured += (buffer, count, _) => Volatile.Read(ref _windowInput)?.Write(buffer, count);
        _windowAudio.ErrorOccurred += (_, message) =>
            Dispatcher.BeginInvoke(() => SetStatus($"Som da janela indisponível: {message}", StatusLevel.Error));

        _suppressLayerEvent = true;
        WindowAudioCheck.IsChecked = _settings.RecordWindowAudio && ProcessAudioCapture.IsSupported;
        if (!ProcessAudioCapture.IsSupported)
        {
            WindowAudioCheck.IsEnabled = false;
            WindowAudioCheck.Content = "Gravar o som da janela (requer Windows 11)";
        }
        LockLayersButton.IsChecked = _settings.LayersLocked;
        LockLayersIcon.Text = _settings.LayersLocked ? "" : "";

        var captureMethods = new List<CaptureMethodItem>
        {
            new(CaptureMethod.Automatic, "Automático"),
            new(CaptureMethod.Wgc, "WGC (sem borda)"),
            new(CaptureMethod.BitBlt, "BitBlt (compatibilidade)"),
        };
        CaptureMethodCombo.ItemsSource = captureMethods;
        CaptureMethodCombo.SelectedItem = captureMethods.FirstOrDefault(m => m.Method == _settings.CaptureMethod) ?? captureMethods[0];
        _suppressLayerEvent = false;

        if (GraphicsCaptureInterop.IsSupported)
            _ = WindowCaptureSource.RequestAccessAsync();
        else
        {
            WindowTargetCombo.IsEnabled = false;
            WindowHintText.Text = "A captura de janelas requer o Windows 10 versão 1903 ou mais recente.";
        }

        PreviewImage.SizeChanged += (_, _) => UpdateSelectionAdorner();
        _ = RestoreLayersAsync();
        UpdateLayerControls();
    }

    // ---- Frame routing (capture threads) ----

    private void OnSourceFrame(string key, CapturedFrame frame)
    {
        if (key == WindowKey)
        {
            uint w = (uint)frame.Bitmap.PixelWidth & ~1u, h = (uint)frame.Bitmap.PixelHeight & ~1u;
            if (_windowFormat is not { } f || f.Width != w || f.Height != h)
                _windowFormat = new VideoFormatInfo(w, h, 60, 1);
        }

        if (key == MainKey)
            OnMainFrame(frame);
        else if (_routes.TryGetValue(key, out var layerId))
            _compositor.SetVideoFrame(layerId, frame);
        else
            frame.Dispose();
    }

    // ---- Publishing the scene ----

    /// <summary>Absolute rectangle of a layer, following the layer it is attached to.</summary>
    private RectN AbsoluteRect(SceneLayer layer, int depth = 0)
    {
        if (layer.AttachedTo is Guid target && depth < 8 && _layers.FirstOrDefault(l => l.Id == target) is { } parent)
            return layer.RelativeRect.Within(AbsoluteRect(parent, depth + 1));
        return layer.Rect;
    }

    /// <summary>
    /// Sends the current scene to the compositor and the frame router; call after any change.
    /// Also keeps the attached layers' absolute rectangles and the selection frame up to date.
    /// </summary>
    private void PublishLayers(bool save = true)
    {
        foreach (var layer in _layers.Where(l => l.AttachedTo is not null))
            layer.Rect = AbsoluteRect(layer);

        var routes = new Dictionary<string, Guid>();
        var drawn = new List<CompositorLayer>();
        foreach (var layer in _layers)
        {
            if (!layer.Visible) continue;
            if (layer.IsVideo) routes[layer.SourceKey] = layer.Id;
            drawn.Add(new CompositorLayer(layer.Id, layer.Rect, layer.Opacity,
                Border: layer.Kind is SceneLayerKind.Camera or SceneLayerKind.Window or SceneLayerKind.Device));
        }

        _routes = routes;
        _compositor.SetLayers(drawn);
        UpdateSelectionAdorner();
        ReplayWindowFrame(); // A still window main picture would not show the change otherwise.

        if (save) SaveLayers();
    }

    private void SaveLayers()
    {
        // Windows and the toolbar source change between sessions: keep only webcams and pictures.
        _settings.Layers = _layers.Where(l => l.IsSaved).ToList();
        ScheduleSave();
    }

    /// <summary>Brings back the saved pictures, and the webcams switched off (they never turn on by themselves).</summary>
    private async Task RestoreLayersAsync()
    {
        foreach (var saved in _settings.Layers.ToList())
        {
            if (saved.Kind == SceneLayerKind.Image)
            {
                if (saved.Source is null || !File.Exists(saved.Source)) continue;
                if (!await LoadPictureAsync(saved)) continue;
            }
            else if (saved.Kind == SceneLayerKind.Camera)
            {
                saved.Visible = false;
            }
            else if (saved.Kind == SceneLayerKind.Media)
            {
                if (saved.Source is null || !File.Exists(saved.Source) || !await StartMediaAsync(saved)) continue;
            }
            else
            {
                continue;
            }
            _layers.Add(saved);
        }

        // Attachments to layers that were not restored fall back to free positioning.
        foreach (var layer in _layers.Where(l => l.AttachedTo is Guid t && _layers.All(o => o.Id != t)))
            layer.AttachedTo = null;

        PublishLayers(save: false);
        UpdateLayerControls();
        if (_layers.Any(l => l.Kind == SceneLayerKind.Camera))
            Log.Info("Camadas repostas; webcams desligadas até as ligar no olho");
    }

    // ---- Popup ----

    private void LayersButton_Click(object sender, RoutedEventArgs e)
    {
        if (!LayersPopup.IsOpen)
        {
            RefreshWindowTargets();
            UpdateLayerControls();
        }
        LayersPopup.IsOpen = !LayersPopup.IsOpen;
    }

    private void RefreshWindowsButton_Click(object sender, RoutedEventArgs e) => RefreshWindowTargets();

    private void RefreshWindowTargets()
    {
        if (!GraphicsCaptureInterop.IsSupported) return;

        IReadOnlyList<CaptureTarget> targets;
        try
        {
            targets = GraphicsCaptureInterop.GetTargets(new WindowInteropHelper(this).Handle);
        }
        catch (Exception ex)
        {
            SetStatus($"Não foi possível listar as janelas: {ex.Message}", StatusLevel.Error);
            return;
        }

        _suppressLayerEvent = true;
        WindowTargetCombo.ItemsSource = targets;
        WindowTargetCombo.SelectedItem = targets.FirstOrDefault(t =>
            _windowTarget is not null && t.Window == _windowTarget.Window && t.Monitor == _windowTarget.Monitor);
        _suppressLayerEvent = false;
    }

    /// <summary>Rebuilds the main-picture and "add layer" lists and the layer list.</summary>
    private void UpdateLayerControls()
    {
        _suppressLayerEvent = true;

        var deviceName = SelectedVideoDevice?.Name ?? "fonte de vídeo";
        var mains = new List<LayerItem>
        {
            new(LayerSource.Device, $"Fonte de vídeo da barra ({deviceName})"),
            new(LayerSource.Window, "Janela ou ecrã (escolha abaixo)"),
        };
        MainLayerCombo.ItemsSource = mains;
        MainLayerCombo.SelectedItem = mains.First(m => m.Kind == MainLayer);

        var adds = new List<AddItem> { new("", null, "＋ Adicionar camada…") };
        if (DevicesCombo.ItemsSource is IEnumerable<VideoSourceItem> devices)
        {
            foreach (var device in devices.Where(d => d.Device.Id != SelectedVideoDevice?.Id &&
                                                      _layers.All(l => l.Kind != SceneLayerKind.Camera || l.Source != d.Device.Id)))
                adds.Add(new("camera", device.Device.Id, $"Webcam: {device.Name}"));
        }
        adds.Add(new("image", null, "Imagem (logótipo, moldura, marca de água)…"));
        adds.Add(new("media", null, "Vídeo animado (WebM, MP4…)…"));
        if (MainLayer != LayerSource.Window && _layers.All(l => l.Kind != SceneLayerKind.Window))
            adds.Add(new("window", null, "Janela ou ecrã escolhido acima"));
        if (MainLayer != LayerSource.Device && _layers.All(l => l.Kind != SceneLayerKind.Device))
            adds.Add(new("device", null, $"Fonte de vídeo da barra ({deviceName})"));
        AddLayerCombo.ItemsSource = adds;
        AddLayerCombo.SelectedIndex = 0;

        _suppressLayerEvent = false;
        RebuildLayerList();
    }

    /// <summary>One row per layer, top layer first, with show/hide, order and delete buttons.</summary>
    private void RebuildLayerList()
    {
        LayerListPanel.Children.Clear();
        NoLayersText.Visibility = _layers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            bool selected = layer == _selectedLayer;

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int c = 0; c < 4; c++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = layer.Kind switch
                {
                    SceneLayerKind.Camera => "",
                    SceneLayerKind.Image => "",
                    SceneLayerKind.Window => "",
                    _ => "",
                },
                FontFamily = (FontFamily)FindResource("IconFont"),
                Foreground = (Brush)FindResource(layer.Visible ? "AccentBrush" : "TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });

            var name = new TextBlock
            {
                Text = layer.AttachedTo is null ? layer.Name : $"{layer.Name}  ⛓",
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(layer.Visible ? "TextPrimaryBrush" : "TextSecondaryBrush"),
                ToolTip = layer.Name,
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            AddRowButton(row, 2, layer.Visible ? "" : "",
                layer.Kind == SceneLayerKind.Camera ? (layer.Visible ? "Desligar a webcam" : "Ligar a webcam")
                                                    : (layer.Visible ? "Esconder" : "Mostrar"),
                () => _ = ToggleLayerVisibleAsync(layer));
            AddRowButton(row, 3, "", "Trazer para a frente", () => MoveLayer(layer, +1), enabled: i < _layers.Count - 1);
            AddRowButton(row, 4, "", "Enviar para trás", () => MoveLayer(layer, -1), enabled: i > 0);
            AddRowButton(row, 5, "", "Remover a camada", () => _ = RemoveLayerAsync(layer));

            var item = new Border
            {
                Child = row,
                Padding = new Thickness(8, 4, 4, 4),
                Margin = new Thickness(0, 0, 0, 4),
                CornerRadius = new CornerRadius(7),
                Background = (Brush)FindResource(selected ? "HighlightBrush" : "CardBrush"),
                BorderBrush = (Brush)FindResource(selected ? "AccentBrush" : "BorderSubtleBrush"),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            item.MouseLeftButtonUp += (_, _) => SelectLayer(layer);
            LayerListPanel.Children.Add(item);
        }

        UpdateLayerDetails();
    }

    private void AddRowButton(Grid row, int column, string glyph, string tip, Action onClick, bool enabled = true)
    {
        var button = new Button
        {
            Style = (Style)FindResource("IconButtonStyle"),
            Width = 28,
            Height = 28,
            Margin = new Thickness(2, 0, 0, 0),
            ToolTip = tip,
            IsEnabled = enabled,
            Background = Brushes.Transparent,
            Content = new TextBlock
            {
                Text = glyph,
                FontSize = 12,
                FontFamily = (FontFamily)FindResource("IconFont"),
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
            },
        };
        button.Click += (_, e) =>
        {
            e.Handled = true; // Not a row click.
            onClick();
        };
        Grid.SetColumn(button, column);
        row.Children.Add(button);
    }

    /// <summary>Shows the selected layer's opacity, attachment and quick positions.</summary>
    private void UpdateLayerDetails()
    {
        var layer = _selectedLayer;
        LayerDetailsPanel.Visibility = layer is null ? Visibility.Collapsed : Visibility.Visible;
        if (layer is null) return;

        _suppressLayerEvent = true;
        LayerDetailsTitle.Text = layer.Name;
        LayerOpacitySlider.Value = layer.Opacity;
        LayerOpacityText.Text = FormatPercent(layer.Opacity);

        // Any layer can follow another one (no loops); typical use: a picture frame around a webcam.
        var attach = new List<AttachItem> { new(null, "Nenhuma (posição livre)") };
        attach.AddRange(_layers.Where(l => l != layer && !DependsOn(l, layer)).Select(l => new AttachItem(l.Id, l.Name)));
        AttachCombo.ItemsSource = attach;
        AttachCombo.SelectedItem = attach.FirstOrDefault(a => a.Target == layer.AttachedTo) ?? attach[0];
        FitToTargetButton.IsEnabled = layer.AttachedTo is not null;

        KeyPanel.Visibility = layer.Kind == SceneLayerKind.Media ? Visibility.Visible : Visibility.Collapsed;
        KeyCombo.SelectedIndex = (int)layer.Key;

        SwapLayersButton.Visibility = layer.Kind is SceneLayerKind.Window or SceneLayerKind.Device
            ? Visibility.Visible : Visibility.Collapsed;
        _suppressLayerEvent = false;
    }

    /// <summary>True if <paramref name="layer"/> follows <paramref name="ancestor"/>, directly or not.</summary>
    private bool DependsOn(SceneLayer layer, SceneLayer ancestor)
    {
        var current = layer;
        for (int depth = 0; depth < 8 && current.AttachedTo is Guid t; depth++)
        {
            if (t == ancestor.Id) return true;
            current = _layers.FirstOrDefault(l => l.Id == t);
            if (current is null) return false;
        }
        return false;
    }

    private void SelectLayer(SceneLayer? layer)
    {
        _selectedLayer = layer;
        RebuildLayerList();
        UpdateSelectionAdorner();
    }

    // ---- Main picture ----

    private async void MainLayerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLayerEvent || MainLayerCombo.SelectedItem is not LayerItem item) return;
        await SetMainAsync(item.Kind);
    }

    private async void WindowTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLayerEvent || WindowTargetCombo.SelectedItem is not CaptureTarget target) return;

        _windowTarget = target;
        if (MainLayer == LayerSource.Window || _layers.Any(l => l.Kind == SceneLayerKind.Window))
            StartWindow(); // Switch to the new window right away.
        else if (MainLayer == LayerSource.Device && !_isStreaming)
            await SetMainAsync(LayerSource.Window); // Nothing on screen yet: show it.
        else
            AddWindowLayer(); // Otherwise, put it over the picture.
    }

    private void CaptureMethodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLayerEvent || CaptureMethodCombo.SelectedItem is not CaptureMethodItem item) return;

        _settings.CaptureMethod = item.Method;
        _settings.Save();
        if (_window.IsRunning) StartWindow(); // Apply to the capture already running.
    }

    private async Task SetMainAsync(LayerSource kind)
    {
        if (kind == LayerSource.Window)
        {
            if (_windowTarget is null)
            {
                UpdateLayerControls();
                SetStatus("Escolha a janela ou o ecrã na lista \"Janela ou ecrã\" das camadas.");
                return;
            }
            if (!_window.IsRunning && !StartWindow())
            {
                UpdateLayerControls();
                return;
            }
        }

        // A source cannot be the main picture and a layer at the same time.
        var duplicate = _layers.FirstOrDefault(l =>
            (kind == LayerSource.Window && l.Kind == SceneLayerKind.Window) ||
            (kind == LayerSource.Device && l.Kind == SceneLayerKind.Device));
        if (duplicate is not null) await RemoveLayerAsync(duplicate, keepWindow: true);

        if (kind == MainLayer)
        {
            UpdateLayerControls();
            return;
        }

        _mainLayer = (int)kind;
        await AfterLayersChangedAsync();

        if (kind == LayerSource.Window)
            SetStatus($"Imagem principal: {_windowTarget!.Title}", StatusLevel.Success);
        else if (!_isStreaming)
            SetStatus("Imagem principal: fonte de vídeo da barra. Clique em Iniciar.");
    }

    private async void SwapLayersButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLayer is not { Kind: SceneLayerKind.Window or SceneLayerKind.Device } layer) return;

        // The layer keeps its place and size; only the sources trade roles.
        var newMain = layer.Kind == SceneLayerKind.Window ? LayerSource.Window : LayerSource.Device;
        layer.Kind = MainLayer == LayerSource.Window ? SceneLayerKind.Window : SceneLayerKind.Device;
        layer.Name = LayerName(layer.Kind);
        layer.Aspect = 0;
        _compositor.ClearContent(layer.Id);
        _mainLayer = (int)newMain;

        await AfterLayersChangedAsync();
        SetStatus(MainLayer == LayerSource.Window
            ? $"Imagem principal: {_windowTarget?.Title}"
            : $"Imagem principal: {_currentDeviceName ?? "fonte de vídeo"}", StatusLevel.Success);
    }

    private string LayerName(SceneLayerKind kind) => kind switch
    {
        SceneLayerKind.Window => _windowTarget?.Title ?? "Janela",
        SceneLayerKind.Device => _currentDeviceName ?? SelectedVideoDevice?.Name ?? "Fonte de vídeo",
        _ => "Camada",
    };

    // ---- Adding layers ----

    private async void AddLayerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLayerEvent || AddLayerCombo.SelectedItem is not AddItem { Action.Length: > 0 } item) return;

        _suppressLayerEvent = true;
        AddLayerCombo.SelectedIndex = 0;
        _suppressLayerEvent = false;

        switch (item.Action)
        {
            case "camera": await AddCameraLayerAsync(item.DeviceId!, item.Label["Webcam: ".Length..]); break;
            case "image": await AddPictureLayerAsync(); break;
            case "media": await AddMediaLayerAsync(); break;
            case "window": AddWindowLayer(); break;
            case "device": AddDeviceLayer(); break;
        }
    }

    /// <summary>A new layer's default place: a corner-sized box in the bottom-right corner.</summary>
    private RectN DefaultRect(double aspect)
    {
        const double width = 0.28, margin = 0.02;
        double height = Math.Min(width * CanvasAspect / Math.Max(aspect, 0.1), 1);
        double marginY = margin * CanvasAspect;
        return new RectN(1 - margin - width, Math.Max(0, 1 - marginY - height), width, height);
    }

    private void AddLayer(SceneLayer layer)
    {
        _layers.Add(layer);
        _selectedLayer = layer;
        PublishLayers();
        UpdateLayerControls();
        UpdateRecordUi();
    }

    private async Task AddCameraLayerAsync(string deviceId, string name)
    {
        var layer = new SceneLayer
        {
            Kind = SceneLayerKind.Camera,
            Source = deviceId,
            Name = name,
            Rect = DefaultRect(16.0 / 9.0),
        };
        if (!await StartCameraAsync(layer)) return;
        layer.Rect = DefaultRect(layer.Aspect); // The webcam's real shape (4:3, 16:9…).
        AddLayer(layer);
    }

    private async Task AddPictureLayerAsync()
    {
        LayersPopup.IsOpen = false;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Escolha uma imagem para sobrepor",
            Filter = "Imagens (PNG, JPG, BMP, GIF)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Todos os ficheiros|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            // Copied into the app's folder: moving or deleting the original does not break the scene.
            Directory.CreateDirectory(AppSettings.PicturesFolder);
            var id = Guid.NewGuid();
            var copy = Path.Combine(AppSettings.PicturesFolder,
                                    id.ToString("N") + Path.GetExtension(dialog.FileName).ToLowerInvariant());
            File.Copy(dialog.FileName, copy);

            var layer = new SceneLayer
            {
                Id = id,
                Kind = SceneLayerKind.Image,
                Source = copy,
                Name = Path.GetFileNameWithoutExtension(dialog.FileName),
            };
            if (!await LoadPictureAsync(layer))
            {
                TryDeleteFile(copy);
                return;
            }

            // Full-picture images (frames, overlays) fill it; smaller ones (logos) start in a corner.
            bool fullFrame = Math.Abs(layer.Aspect - CanvasAspect) < 0.05;
            layer.Rect = fullFrame ? new RectN(0, 0, 1, 1) : PlaceInCorner(DefaultRect(layer.Aspect), "TopLeft");
            AddLayer(layer);
            SetStatus($"Imagem adicionada: {layer.Name}. Arraste-a na imagem para a posicionar.", StatusLevel.Success);
        }
        catch (Exception ex)
        {
            Log.Error("Erro ao adicionar a imagem", ex);
            SetStatus($"Não foi possível usar esta imagem: {ex.Message}", StatusLevel.Error);
        }
    }

    /// <summary>Decodes a picture layer's file (keeping transparency) and hands it to the compositor.</summary>
    private async Task<bool> LoadPictureAsync(SceneLayer layer)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(layer.Source);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            _compositor.SetPicture(layer.Id, bitmap);
            layer.Aspect = (double)bitmap.PixelWidth / bitmap.PixelHeight;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Erro ao ler a imagem {layer.Source}", ex);
            SetStatus($"Não foi possível ler a imagem \"{layer.Name}\": {ex.Message}", StatusLevel.Error);
            return false;
        }
    }

    private void AddWindowLayer()
    {
        if (_windowTarget is null)
        {
            SetStatus("Escolha primeiro a janela ou o ecrã na lista \"Janela ou ecrã\".");
            return;
        }
        if (MainLayer == LayerSource.Window || _layers.Any(l => l.Kind == SceneLayerKind.Window)) return;
        if (!_window.IsRunning && !StartWindow()) return;

        AddLayer(new SceneLayer { Kind = SceneLayerKind.Window, Name = _windowTarget.Title, Rect = DefaultRect(16.0 / 9.0) });
    }

    private void AddDeviceLayer()
    {
        if (MainLayer == LayerSource.Device || _layers.Any(l => l.Kind == SceneLayerKind.Device)) return;
        AddLayer(new SceneLayer
        {
            Kind = SceneLayerKind.Device,
            Name = LayerName(SceneLayerKind.Device),
            Rect = DefaultRect(16.0 / 9.0),
        });
        if (!_isStreaming)
            SetStatus("Clique em Iniciar para ver a fonte de vídeo na camada.");
    }

    // ---- Changing and removing layers ----

    private async Task ToggleLayerVisibleAsync(SceneLayer layer)
    {
        if (layer.Kind == SceneLayerKind.Media && _media.TryGetValue(layer.Id, out var player))
        {
            if (layer.Visible) player.Pause(); else player.Resume();
        }
        if (layer.Kind == SceneLayerKind.Camera)
        {
            // A webcam layer's eye switches the webcam itself on or off.
            if (layer.Visible) await StopCameraAsync(layer);
            else if (!await StartCameraAsync(layer)) return;
        }
        layer.Visible = !layer.Visible;
        PublishLayers();
        RebuildLayerList();
    }

    private void MoveLayer(SceneLayer layer, int delta)
    {
        int index = _layers.IndexOf(layer), target = index + delta;
        if (index < 0 || target < 0 || target >= _layers.Count) return;
        _layers.RemoveAt(index);
        _layers.Insert(target, layer);
        PublishLayers();
        RebuildLayerList();
    }

    private async Task RemoveLayerAsync(SceneLayer layer, bool keepWindow = false)
    {
        // Layers following this one stay where they are, now free.
        foreach (var child in _layers.Where(l => l.AttachedTo == layer.Id))
        {
            child.Rect = AbsoluteRect(child);
            child.AttachedTo = null;
        }

        _layers.Remove(layer);
        _compositor.ClearContent(layer.Id);
        if (_selectedLayer == layer) _selectedLayer = null;

        if (layer.Kind == SceneLayerKind.Camera) await StopCameraAsync(layer);
        if (layer.Kind == SceneLayerKind.Media && _media.Remove(layer.Id, out var media)) media.Dispose();
        if (layer.Kind is SceneLayerKind.Image or SceneLayerKind.Media && layer.Source is not null) TryDeleteFile(layer.Source);
        if (layer.Kind == SceneLayerKind.Window && !keepWindow && MainLayer != LayerSource.Window) _window.Stop();

        PublishLayers();
        UpdateLayerControls();
        await UpdateWindowAudioAsync();
        UpdateRecordUi();
    }

    private void LayerOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressLayerEvent || _selectedLayer is not { } layer) return;
        layer.Opacity = e.NewValue;
        LayerOpacityText.Text = FormatPercent(e.NewValue);
        PublishLayers();
    }

    private void AttachCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLayerEvent || _selectedLayer is not { } layer || AttachCombo.SelectedItem is not AttachItem item) return;

        var absolute = AbsoluteRect(layer);
        layer.AttachedTo = item.Target;
        if (item.Target is Guid target && _layers.FirstOrDefault(l => l.Id == target) is { } parent)
            layer.RelativeRect = absolute.RelativeTo(AbsoluteRect(parent)); // Stays where it is, now following.
        else
            layer.Rect = absolute;

        PublishLayers();
        RebuildLayerList();
    }

    /// <summary>Puts the picture around the layer it follows, slightly larger: the usual webcam frame.</summary>
    private void FitToTargetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLayer is not { AttachedTo: not null } layer) return;
        const double frame = 0.06;
        layer.RelativeRect = new RectN(-frame, -frame, 1 + 2 * frame, 1 + 2 * frame);
        PublishLayers();
    }

    private void QuickPosition_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLayer is not { } layer || sender is not FrameworkElement { Tag: string where }) return;
        SetLayerRect(layer, PlaceInCorner(AbsoluteRect(layer), where));
        PublishLayers();
    }

    /// <summary>The same size, moved to a corner (with the usual margin), the centre, or filling the picture.</summary>
    private RectN PlaceInCorner(RectN rect, string where)
    {
        const double margin = 0.02;
        double marginY = margin * CanvasAspect;
        return where switch
        {
            "TopLeft" => rect with { X = margin, Y = marginY },
            "TopRight" => rect with { X = 1 - margin - rect.W, Y = marginY },
            "BottomLeft" => rect with { X = margin, Y = 1 - marginY - rect.H },
            "BottomRight" => rect with { X = 1 - margin - rect.W, Y = 1 - marginY - rect.H },
            "Center" => rect with { X = (1 - rect.W) / 2, Y = (1 - rect.H) / 2 },
            "Fill" => new RectN(0, 0, 1, 1),
            _ => rect,
        };
    }

    /// <summary>Moves/resizes a layer; an attached one keeps following, with the new offset.</summary>
    private void SetLayerRect(SceneLayer layer, RectN absolute)
    {
        if (layer.AttachedTo is Guid target && _layers.FirstOrDefault(l => l.Id == target) is { } parent)
            layer.RelativeRect = absolute.RelativeTo(AbsoluteRect(parent));
        layer.Rect = absolute;
    }

    private void LockLayersButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.LayersLocked = LayersLocked;
        _settings.Save();
        LockLayersIcon.Text = LayersLocked ? "" : "";
        UpdateSelectionAdorner();
    }

    // ---- Sources ----

    private async Task<bool> StartCameraAsync(SceneLayer layer)
    {
        var deviceId = layer.Source!;
        if (deviceId == _currentDeviceId)
        {
            SetStatus("Esta câmera já é a fonte de vídeo da barra: escolha outra.", StatusLevel.Error);
            return false;
        }

        SetStatus($"A ligar a webcam {layer.Name}…");
        var camera = new VideoCaptureService();
        try
        {
            var formats = await camera.OpenDeviceAsync(deviceId);
            if (formats.Count == 0) throw new InvalidOperationException("a câmera não anunciou nenhum formato");

            // Small and compressed: a layer rarely fills the picture, and MJPG leaves USB
            // bandwidth for the capture card.
            var format = formats
                .Where(f => f.Height <= 720 && f.Fps >= 24)
                .OrderByDescending(f => f.Height)
                .ThenBy(f => Math.Abs(f.Fps - 30))
                .ThenBy(f => f.IsCompressed ? 0 : 1)
                .FirstOrDefault() ?? formats.MinBy(f => Math.Abs((int)f.Height - 720))!;

            var key = layer.SourceKey;
            camera.FrameArrived += (_, frame) => OnSourceFrame(key, frame);
            camera.ErrorOccurred += (_, message) => Dispatcher.BeginInvoke(async () =>
            {
                if (_layers.Contains(layer) && layer.Visible) await ToggleLayerVisibleAsync(layer);
                SetStatus($"A webcam {layer.Name} parou: {message}", StatusLevel.Error);
            });
            await camera.StartAsync(format);

            _cameras[deviceId] = camera;
            layer.Aspect = (double)format.Width / format.Height;
            SetStatus($"Webcam ligada: {layer.Name} — {format}", StatusLevel.Success);
            Log.Info($"Webcam {layer.Name}: {format} ({format.Subtype})");
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            await camera.CloseAsync();
            SetStatus("Acesso negado à webcam. Permita o acesso à câmera nas Definições do Windows > Privacidade.",
                      StatusLevel.Error);
        }
        catch (Exception ex)
        {
            await camera.CloseAsync();
            Log.Error("Erro ao ligar a webcam", ex);
            SetStatus($"Não foi possível ligar a webcam: {ex.Message}. Se estiver na mesma porta USB que a placa, " +
                      "experimente outra porta (pode faltar largura de banda).", StatusLevel.Error);
        }
        return false;
    }

    private async Task StopCameraAsync(SceneLayer layer)
    {
        if (layer.Source is not null && _cameras.Remove(layer.Source, out var camera))
            await camera.CloseAsync();
        _compositor.ClearContent(layer.Id);
    }

    /// <summary>A device cannot be opened twice: if the toolbar source is a layer's webcam, switch that webcam off.</summary>
    private async Task ReleaseCameraForDeviceAsync(string deviceId)
    {
        foreach (var layer in _layers.Where(l => l.Kind == SceneLayerKind.Camera && l.Source == deviceId && l.Visible).ToList())
            await ToggleLayerVisibleAsync(layer);
    }

    /// <summary>After a device change: switch off webcams that were unplugged.</summary>
    private async Task ReconcileCamerasAsync(IReadOnlyCollection<string> presentDeviceIds)
    {
        foreach (var layer in _layers.Where(l => l.Kind == SceneLayerKind.Camera && l.Visible &&
                                                 !presentDeviceIds.Contains(l.Source!)).ToList())
        {
            await ToggleLayerVisibleAsync(layer);
            SetStatus($"A webcam {layer.Name} foi desligada.", StatusLevel.Error);
        }
        UpdateLayerControls();
    }

    /// <summary>The toolbar source stopped: its layer (if any) has nothing to show.</summary>
    private void ClearDeviceLayerContent()
    {
        foreach (var layer in _layers.Where(l => l.Kind == SceneLayerKind.Device))
            _compositor.ClearContent(layer.Id);
    }

    private bool StartWindow()
    {
        if (_windowTarget is null) return false;
        try
        {
            _windowFormat = null;
            _minimizedWarned = false;
            _window.Start(_windowTarget, _settings.CaptureMethod);
            if (MainLayer == LayerSource.Window) NoSignalText.Visibility = Visibility.Collapsed;
            foreach (var layer in _layers.Where(l => l.Kind == SceneLayerKind.Window))
                layer.Name = _windowTarget.Title;
            _ = UpdateWindowAudioAsync();
            return true;
        }
        catch (Exception ex)
        {
            _window.Stop();
            SetStatus($"Não foi possível capturar \"{_windowTarget.Title}\": {ex.Message}", StatusLevel.Error);
            return false;
        }
    }

    /// <summary>Stops what nothing uses any more and refreshes the screen, recording and controls.</summary>
    private async Task AfterLayersChangedAsync()
    {
        if (MainLayer != LayerSource.Window && _layers.All(l => l.Kind != SceneLayerKind.Window) && _window.IsRunning)
        {
            _window.Stop();
            _windowFormat = null;
        }

        if (!MainActive)
            ClearPreview();

        PublishLayers();
        await UpdateWindowAudioAsync();
        UpdateLayerControls();
        UpdateRecordingOptions();
        UpdateRecordUi();
    }

    private async void OnWindowClosed()
    {
        var title = _windowTarget?.Title;
        _windowTarget = null;
        _window.Stop();
        _windowFormat = null;

        if (MainLayer == LayerSource.Window)
            _mainLayer = (int)LayerSource.Device;
        foreach (var layer in _layers.Where(l => l.Kind == SceneLayerKind.Window).ToList())
            await RemoveLayerAsync(layer, keepWindow: true);

        await AfterLayersChangedAsync();
        SetStatus($"A janela capturada foi fechada ({title}).", StatusLevel.Error);
    }

    /// <summary>
    /// Sends the window's latest picture through the scene again, so a still window shows up
    /// right away after a change (it only sends frames when its content changes).
    /// </summary>
    private void ReplayWindowFrame()
    {
        if (!_window.IsRunning) return;
        if (MainLayer != LayerSource.Window && !_routes.ContainsKey(WindowKey)) return;
        if (_window.RepeatLastFrame() is CapturedFrame frame)
            OnSourceFrame(WindowKey, frame);
    }

    /// <summary>Every 0.5 s: video layers' real shape, still windows while recording, minimised warning.</summary>
    private void CheckWindowState()
    {
        // A webcam's real shape is known once it sends a frame: keep the layer's width, fix its height.
        // Window/screen capture is excluded: its reported size can jitter a little (BitBlt, monitor
        // capture), and the user resizes/crops that layer by hand, so re-locking its aspect here would
        // undo that drag every half second.
        bool changed = false;
        foreach (var layer in _layers.Where(l => l.Kind is SceneLayerKind.Camera or SceneLayerKind.Device && l.Visible))
        {
            if (_compositor.ContentSize(layer.Id) is not { } size || size.Height == 0) continue;
            double aspect = (double)size.Width / size.Height;
            if (Math.Abs(aspect - layer.Aspect) < 0.01) continue;
            layer.Aspect = aspect;
            var rect = AbsoluteRect(layer);
            SetLayerRect(layer, rect with { H = rect.W * CanvasAspect / aspect });
            changed = true;
        }
        if (changed) PublishLayers();

        if (!_window.IsRunning) return;

        // A recording of a still page would otherwise get no frames at all for a long time.
        if (IsRecording && _window.LastFrameTime is TimeSpan last && Services.MediaClock.Now - last > TimeSpan.FromSeconds(0.5))
            ReplayWindowFrame();

        if (_window.IsMinimized)
        {
            if (_minimizedWarned) return;
            _minimizedWarned = true;
            SetStatus("A janela capturada está minimizada: a imagem fica parada até a restaurar.", StatusLevel.Error);
        }
        else
        {
            _minimizedWarned = false;
        }
    }

    // ---- The window's sound, for recordings ----

    private void WindowAudioCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.RecordWindowAudio = WindowAudioCheck.IsChecked == true;
        _settings.Save();
        _ = UpdateWindowAudioAsync();
    }

    /// <summary>
    /// Listens to the window's app (or, for a whole screen, everything except LCapture) only
    /// while recording, and feeds it to the recording's mixer.
    /// </summary>
    private async Task UpdateWindowAudioAsync()
    {
        (int Pid, bool Exclude)? wanted = null;
        if (IsRecording && _window.IsRunning && _window.Target is { } target &&
            WindowAudioCheck.IsChecked == true && ProcessAudioCapture.IsSupported)
        {
            wanted = target.IsMonitor ? (Environment.ProcessId, true) : (target.ProcessId, false);
        }

        if (wanted == _windowAudioTarget) return;

        _windowAudio.Stop();
        _windowAudioTarget = null;
        DetachWindowAudio();
        if (wanted is not { } w) return;

        try
        {
            AttachWindowAudio();
            await _windowAudio.StartAsync(w.Pid, w.Exclude);
            _windowAudioTarget = wanted;
        }
        catch (Exception ex)
        {
            DetachWindowAudio();
            SetStatus($"Não foi possível gravar o som da janela: {ex.Message}", StatusLevel.Error);
        }
    }

    private async Task StopLayersAsync()
    {
        _windowAudio.Stop();
        _window.Stop();
        foreach (var camera in _cameras.Values.ToList())
            await camera.CloseAsync();
        _cameras.Clear();
        foreach (var media in _media.Values) media.Dispose();
        _media.Clear();
    }

    // ---- Video files ----

    private async Task AddMediaLayerAsync()
    {
        LayersPopup.IsOpen = false;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Escolha um vídeo para sobrepor (em ciclo, sem som)",
            Filter = "Vídeos (WebM, MP4, MOV, MKV)|*.webm;*.mp4;*.mov;*.mkv;*.m4v|Todos os ficheiros|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        Directory.CreateDirectory(AppSettings.PicturesFolder);
        var id = Guid.NewGuid();
        var copy = Path.Combine(AppSettings.PicturesFolder, id.ToString("N") + Path.GetExtension(dialog.FileName).ToLowerInvariant());
        File.Copy(dialog.FileName, copy);

        // WebM usually means a transparent overlay, and Windows turns its transparency white.
        var layer = new SceneLayer
        {
            Id = id,
            Kind = SceneLayerKind.Media,
            Source = copy,
            Name = Path.GetFileNameWithoutExtension(dialog.FileName),
            Key = copy.EndsWith(".webm") ? KeyColor.White : KeyColor.None,
        };
        if (!await StartMediaAsync(layer))
        {
            TryDeleteFile(copy);
            return;
        }
        layer.Rect = DefaultRect(layer.Aspect);
        AddLayer(layer);
        SetStatus($"Vídeo adicionado: {layer.Name}. Botão direito → Ajustar ao ecrã, ou arraste-o.", StatusLevel.Success);
    }

    private async Task<bool> StartMediaAsync(SceneLayer layer)
    {
        var media = new MediaFileSource();
        try
        {
            await media.StartAsync(layer.Source!);
        }
        catch (Exception ex)
        {
            media.Dispose();
            Log.Error($"Erro ao abrir o vídeo {layer.Source}", ex);
            SetStatus($"Não foi possível usar o vídeo \"{layer.Name}\": {ex.Message}", StatusLevel.Error);
            return false;
        }

        var id = layer.Id;
        media.FrameArrived += (_, frame) =>
        {
            if (_routes.ContainsKey(layer.SourceKey)) _compositor.SetVideoFrame(id, frame, layer.Key);
            else frame.Dispose();
        };
        if (!layer.Visible) media.Pause();
        layer.Aspect = (double)media.Width / media.Height;
        _media[id] = media;
        return true;
    }

    private void KeyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLayerEvent || _selectedLayer is not { Kind: SceneLayerKind.Media } layer || KeyCombo.SelectedIndex < 0) return;
        layer.Key = (KeyColor)KeyCombo.SelectedIndex;
        SaveLayers();
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}

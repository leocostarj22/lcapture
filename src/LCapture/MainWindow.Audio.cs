using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LCapture.Services;
using NAudio.CoreAudioApi;

namespace LCapture;

/// <summary>
/// The audio side panel: the capture card's channel (played back and recorded) and a microphone
/// (recorded only, never played back). The microphone is only open while the panel is visible
/// (for its meter) or during a recording.
/// </summary>
public partial class MainWindow
{
    private const string MicGlyph = "";
    private const string MicOffGlyph = "";

    private readonly MicrophoneService _mic = new();
    private MixerInput? _micInput;
    private DispatcherTimer? _meterTimer;
    private bool _suppressMicEvent;
    private double _cardMeterLevel, _micMeterLevel;

    private sealed record MicItem(MMDevice? Device, string Label)
    {
        public override string ToString() => Label;
    }

    private MMDevice? SelectedMic => (MicDevicesCombo.SelectedItem as MicItem)?.Device;

    private bool AudioPanelVisible => AudioPanel.Visibility == Visibility.Visible;

    private void InitAudioPanel()
    {
        _mic.DataCaptured += (buffer, count, _) => Volatile.Read(ref _micInput)?.Write(buffer, count);
        _mic.ErrorOccurred += (_, message) => Dispatcher.BeginInvoke(() =>
        {
            _mic.Stop();
            SetStatus($"O microfone parou: {message}", StatusLevel.Error);
        });

        _suppressMicEvent = true;
        MicVolumeSlider.Value = Math.Clamp(_settings.MicVolume, 0, 1);
        MicMuteButton.IsChecked = _settings.MicMuted;
        MicRecordCheck.IsChecked = _settings.MicRecord;
        WindowAudioVolumeSlider.Value = Math.Clamp(_settings.WindowAudioVolume, 0, 1);
        WindowAudioMuteButton.IsChecked = _settings.WindowAudioMuted;
        _suppressMicEvent = false;
        UpdateMicMuteIcon();
        UpdateWindowAudioMuteIcon();
        UpdateVolumeTexts();

        AudioPanelButton.IsChecked = _settings.AudioPanelOpen;
        LoadMicDevices();

        _meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _meterTimer.Tick += (_, _) => UpdateMeters();
        _meterTimer.Start();
    }

    /// <summary>Fills the microphone list. "Nenhum" by default: a microphone is only used if chosen.</summary>
    private void LoadMicDevices()
    {
        IReadOnlyList<MMDevice> inputs;
        try
        {
            inputs = AudioLoopbackService.GetInputDevices();
        }
        catch (Exception ex)
        {
            Log.Error("Erro ao listar microfones", ex);
            inputs = [];
        }

        var currentId = SelectedMic?.ID ?? _settings.MicDeviceId;
        var items = new List<MicItem> { new(null, "Nenhum") };
        items.AddRange(inputs.Select(d => new MicItem(d, d.FriendlyName)));

        _suppressMicEvent = true;
        MicDevicesCombo.ItemsSource = items;
        MicDevicesCombo.SelectedItem = items.FirstOrDefault(i => i.Device?.ID == currentId) ?? items[0];
        _suppressMicEvent = false;

        UpdateMicState();
    }

    // ---- Panel ----

    private void AudioPanelButton_Click(object sender, RoutedEventArgs e) => SetAudioPanelOpen(AudioPanelButton.IsChecked == true);

    private void CloseAudioPanel_Click(object sender, RoutedEventArgs e) => SetAudioPanelOpen(false);

    private void SetAudioPanelOpen(bool open)
    {
        AudioPanelButton.IsChecked = open;
        _settings.AudioPanelOpen = open;
        _settings.Save();
        UpdateToolbarLayout();
    }

    // ---- Card channel ----

    private void UpdateVolumeTexts()
    {
        VolumeText.Text = FormatPercent(VolumeSlider.Value);
        MicVolumeText.Text = FormatPercent(MicVolumeSlider.Value);
        WindowAudioVolumeText.Text = FormatPercent(WindowAudioVolumeSlider.Value);
    }

    // ---- Window/screen app sound ----

    private float WindowAudioGain => WindowAudioMuteButton.IsChecked == true ? 0f : (float)WindowAudioVolumeSlider.Value;

    private void UpdateWindowAudioGain()
    {
        if (_windowInput is MixerInput input) input.Gain = WindowAudioGain;
    }

    private void UpdateWindowAudioMuteIcon() =>
        WindowAudioMuteIcon.Text = WindowAudioMuteButton.IsChecked == true ? MuteGlyph : VolumeGlyph;

    private void WindowAudioMuteButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.WindowAudioMuted = WindowAudioMuteButton.IsChecked == true;
        _settings.Save();
        UpdateWindowAudioMuteIcon();
        UpdateWindowAudioGain();
    }

    private void WindowAudioVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        UpdateVolumeTexts();
        UpdateWindowAudioGain();

        _settings.WindowAudioVolume = (float)e.NewValue;
        ScheduleSave();
    }

    // ---- Microphone channel ----

    private void MicDevicesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressMicEvent) return;
        _settings.MicDeviceId = SelectedMic?.ID;
        _settings.Save();
        UpdateMicState();
    }

    private void MicMuteButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.MicMuted = MicMuteButton.IsChecked == true;
        _settings.Save();
        UpdateMicMuteIcon();
        UpdateMicGain();
    }

    private void UpdateMicMuteIcon() => MicMuteIcon.Text = MicMuteButton.IsChecked == true ? MicOffGlyph : MicGlyph;

    private void MicVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _suppressMicEvent) return;
        _settings.MicVolume = (float)e.NewValue;
        ScheduleSave();
        UpdateVolumeTexts();
        UpdateMicGain();
    }

    private void MicRecordCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.MicRecord = MicRecordCheck.IsChecked == true;
        _settings.Save();
        UpdateMicState();
    }

    private float MicGain => MicMuteButton.IsChecked == true ? 0f : (float)MicVolumeSlider.Value;

    private void UpdateMicGain()
    {
        if (_micInput is MixerInput input) input.Gain = MicGain;
    }

    /// <summary>
    /// Opens the chosen microphone only while it is needed — the panel is open (meter) or a
    /// recording includes it — and keeps the recording's mixer input in step.
    /// </summary>
    private void UpdateMicState()
    {
        var device = SelectedMic;
        bool recordIt = IsRecording && MicRecordCheck.IsChecked == true;
        bool wanted = device is not null && (AudioPanelVisible || recordIt);

        if (!wanted)
        {
            if (_mic.IsRunning) Log.Info("Microfone desligado");
            DetachMicAudio();
            _mic.Stop();
            return;
        }

        if (!_mic.IsRunning || _mic.DeviceId != device!.ID)
        {
            DetachMicAudio();
            try
            {
                _mic.Start(device!);
                Log.Info($"Microfone ligado: {device!.FriendlyName} ({_mic.Format})");
            }
            catch (Exception ex)
            {
                Log.Error("Erro ao ligar o microfone", ex);
                SetStatus($"Não foi possível usar o microfone: {ex.Message}. Verifique em Definições do Windows > " +
                          "Privacidade e segurança > Microfone.", StatusLevel.Error);
                return;
            }
        }

        if (recordIt && _micInput is null) AttachMicAudio();
        else if (!recordIt) DetachMicAudio();
    }

    private void AttachMicAudio()
    {
        DetachMicAudio();
        if (_recorder is not RecordingService recorder || _mic.Format is not { } format) return;

        var input = recorder.AddAudioInput(format);
        if (input is not null) input.Gain = MicGain;
        Volatile.Write(ref _micInput, input);
    }

    private void DetachMicAudio()
    {
        var input = Interlocked.Exchange(ref _micInput, null);
        _recorder?.RemoveAudioInput(input);
    }

    // ---- Meters ----

    private void UpdateMeters()
    {
        if (!AudioPanelVisible) return;

        // The card meter shows what you hear (after volume/mute); the mic meter shows the
        // microphone itself, so you can see it works even when muted in the recording.
        float card = _audio.Peak * (MuteButton.IsChecked == true ? 0f : (float)VolumeSlider.Value);
        float mic = _mic.IsRunning ? _mic.Peak : 0f;
        _cardMeterLevel = Smooth(_cardMeterLevel, ToMeter(card));
        _micMeterLevel = Smooth(_micMeterLevel, ToMeter(mic));
        DrawMeter(CardMeter, _cardMeterLevel);
        DrawMeter(MicMeter, _micMeterLevel);
    }

    /// <summary>Peak 0..1 to a meter position on a -60..0 dB scale.</summary>
    private static double ToMeter(float peak) =>
        peak <= 0.001f ? 0 : Math.Clamp((20 * Math.Log10(peak) + 60) / 60, 0, 1);

    // Rises at once, falls gently: the usual feel of a level meter.
    private static double Smooth(double shown, double target) => target >= shown ? target : shown * 0.85 + target * 0.15;

    private void DrawMeter(Rectangle fill, double level)
    {
        if (fill.Parent is not FrameworkElement track) return;
        fill.Width = Math.Max(0, track.ActualWidth * level);
        fill.Fill = (Brush)FindResource(level > 0.95 ? "DangerBrush" : level > 0.8 ? "WarningBrush" : "SuccessBrush");
    }
}

using HamDigiSharp.Abstractions;
using HamDigiSharp.Models;
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Windows;
using System.Windows.Automation;
using IOPath = System.IO.Path;

namespace HamDigiSharp.Demo;

public partial class MainWindow
{
    private WaveOutEvent?             _waveOut;
    private FloatArraySampleProvider? _floatProvider;
    private bool                      _isPlaying;
    private CancellationTokenSource?  _txDelayCts;   // cancels "wait for next period" delay

    // ── Transmit button ───────────────────────────────────────────────────────

    private void BtnTransmit_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying) { StopPlaybackWithFade(); return; }

        string message = txtMessage.Text.Trim();
        if (string.IsNullOrEmpty(message))
        {
            MessageBox.Show(this, "Please enter a message to transmit.",
                "No Message", MessageBoxButton.OK, MessageBoxImage.Warning);
            txtMessage.Focus();
            return;
        }

        var proto   = SelectedProtocol;
        var encoder = proto.CreateEncoder();

        if (encoder is null)
        {
            MessageBox.Show(this, $"Encoding is not yet supported for {proto.Name}.",
                "Unsupported Mode", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        float[] pcm;
        try
        {
            pcm = encoder.Encode(message, new EncoderOptions { FrequencyHz = 1000.0, Amplitude = 0.9 });
        }
        catch (Exception ex) { ShowError("Encode Error", ex.Message); return; }

        if (rbWavFile.IsChecked == true) SaveEncodedWav(pcm, proto);
        else                             PlayEncodedAudio(pcm, proto);
    }

    // ── Play ─────────────────────────────────────────────────────────────────

    private void PlayEncodedAudio(float[] pcm, IProtocol proto)
    {
        _isPlaying = true;
        btnTransmit.Content = "⏹ Stop _Playback";
        AutomationProperties.SetName(btnTransmit, "Stop Playback");
        AutomationProperties.SetHelpText(btnTransmit, "Stop the current transmission");

        // Only hold for the period boundary when we're actively decoding — otherwise
        // the user just wants to hear the audio immediately (no radio sync needed).
        bool syncToUtc = IsDecoding;

        _txDelayCts?.Dispose();
        _txDelayCts = new CancellationTokenSource();
        var ct = _txDelayCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                if (syncToUtc)
                {
                    var now     = DateTimeOffset.UtcNow;
                    var elapsed = (now - proto.PeriodStart(now)).TotalSeconds;
                    var sigDur  = proto.TransmitDuration.TotalSeconds;

                    // Transmit immediately if the signal still fits in the current period
                    // (with 200 ms guard for audio latency); otherwise wait for next boundary.
                    double delaySecs = (elapsed + sigDur + 0.2 <= proto.PeriodDuration.TotalSeconds)
                        ? 0.0
                        : (proto.NextPeriodStart(now) - now).TotalSeconds;

                    if (delaySecs > 0 && delaySecs < 0.1) delaySecs += proto.PeriodDuration.TotalSeconds;

                    if (delaySecs > 0)
                    {
                        Dispatcher.Invoke(() => SetStatus(
                            $"TX in {delaySecs:F1} s (next period boundary)…"));
                        await Task.Delay(TimeSpan.FromSeconds(delaySecs), ct);
                    }
                }

                ct.ThrowIfCancellationRequested();

                Dispatcher.Invoke(() => SetStatus($"Playing {proto.Name} transmission…"));

                _waveOut?.Dispose();
                _waveOut       = new WaveOutEvent { DeviceNumber = 0 };
                _floatProvider = new FloatArraySampleProvider(pcm, proto.SampleRate, channels: 1);
                _waveOut.Init(new SampleToWaveProvider16(_floatProvider));
                _waveOut.PlaybackStopped += (_, _) =>
                    Dispatcher.Invoke(() => OnPlaybackStopped("Playback complete"));
                _waveOut.Play();
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() => OnPlaybackStopped("TX cancelled"));
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => { OnPlaybackStopped("Ready"); ShowError("Playback Error", ex.Message); });
            }
        });
    }

    private void StopPlaybackWithFade()
    {
        _txDelayCts?.Cancel();
        _floatProvider?.BeginFadeOut();
    }

    private void OnPlaybackStopped(string status)
    {
        _isPlaying = false;
        _txDelayCts?.Dispose();
        _txDelayCts = null;
        UpdateTransmitButton();
        SetStatus(status);
    }

    // ── Save WAV ──────────────────────────────────────────────────────────────

    private void SaveEncodedWav(float[] pcm, IProtocol proto)
    {
        var dlg = new SaveFileDialog
        {
            Title      = "Save Encoded Audio",
            Filter     = "WAV files (*.wav)|*.wav",
            DefaultExt = "wav",
            FileName   = $"tx_{proto.Name}_{DateTime.UtcNow:HHmmss}.wav",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            using var writer = new WaveFileWriter(
                dlg.FileName, WaveFormat.CreateIeeeFloatWaveFormat(proto.SampleRate, 1));
            writer.WriteSamples(pcm, 0, pcm.Length);
            SetStatus($"Saved: {IOPath.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex) { ShowError("Save Error", ex.Message); }
    }
}
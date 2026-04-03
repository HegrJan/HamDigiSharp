using HamDigiSharp.Abstractions;
using HamDigiSharp.Dsp;
using HamDigiSharp.Engine;
using HamDigiSharp.Models;
using Microsoft.Win32;
using NAudio.Wave;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace HamDigiSharp.Demo;

public partial class MainWindow
{
    private RealTimeDecoder?          _rtDecoder;
    private WaveInEvent?              _waveIn;
    private System.Threading.Timer?  _periodBeepTimer;
    private CancellationTokenSource? _wavDecodeCts;
    private ScrollViewer?            _decodedSv;

    private const int MaxLogBlocks = 2000;
    private const int TrimBlocks   = 200;

    private bool IsDecoding => _waveIn != null || _wavDecodeCts != null;

    // ── Audio source ──────────────────────────────────────────────────────────

    private void RbAudioSource_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateTransmitButton();
        btnBrowse.IsEnabled = rbWavFile.IsChecked == true && !IsDecoding;
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Select WAV File to Decode",
            Filter = "WAV files (*.wav)|*.wav|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            txtWavPath.Text = dlg.FileName;
            SetStatus($"File selected: {IOPath.GetFileName(dlg.FileName)}");
        }
    }

    // ── Start / Stop ──────────────────────────────────────────────────────────

    private void BtnStartStop_Click(object sender, RoutedEventArgs e)
    {
        if (IsDecoding) StopDecoding();
        else            StartDecoding();
    }

    private void StartDecoding()
    {
        if (rbRealTime.IsChecked == true) StartRealTimeDecoding();
        else                              StartWavFileDecoding();
    }

    private void StopDecoding()
    {
        if (_waveIn       != null) StopRealTimeDecoding();
        if (_wavDecodeCts != null) StopWavFileDecoding();
    }

    // ── Real-time ─────────────────────────────────────────────────────────────

    private void StartRealTimeDecoding()
    {
        const int CaptureRate = 48000;
        try
        {
            var proto     = SelectedProtocol;
            int deviceNum = cboAudioDevice.SelectedIndex >= 0 ? cboAudioDevice.SelectedIndex : 0;

            // IProtocol overload: sets FreqLow/FreqHigh automatically from protocol defaults
            _rtDecoder = new RealTimeDecoder(proto, CaptureRate) { AlignToUtc = true };
            _rtDecoder.PeriodDecoded += OnPeriodDecoded;
            _rtDecoder.DecodeError   += OnDecodeError;

            _waveIn = new WaveInEvent
                { WaveFormat = new WaveFormat(CaptureRate, 16, 1), DeviceNumber = deviceNum, BufferMilliseconds = 50 };
            _waveIn.DataAvailable    += WaveIn_DataAvailable;
            _waveIn.RecordingStopped += WaveIn_RecordingStopped;
            _waveIn.StartRecording();
            StartPeriodBeepTimer(proto);

            string deviceName = deviceNum < WaveIn.DeviceCount
                ? WaveIn.GetCapabilities(deviceNum).ProductName : "default";
            SetDecodingState(true, $"Decoding {proto.Name} in real-time [{deviceName}] — waiting for next period…");
        }
        catch (Exception ex) { CleanupRealTime(); ShowError("Audio capture error", ex.Message); }
    }

    private void StopRealTimeDecoding() => _waveIn?.StopRecording();

    private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
    {
        int count   = e.BytesRecorded / 2;
        float[] buf = new float[count];
        for (int i = 0; i < count; i++)
            buf[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
        _rtDecoder?.AddSamples(buf);
    }

    private void WaveIn_RecordingStopped(object? sender, StoppedEventArgs e)
        => Dispatcher.BeginInvoke(CleanupRealTime);

    private void CleanupRealTime()
    {
        StopPeriodBeepTimer();
        _waveIn?.Dispose();    _waveIn    = null;
        _rtDecoder?.Dispose(); _rtDecoder = null;
        SetDecodingState(false, "Ready");
    }

    // ── WAV file ──────────────────────────────────────────────────────────────

    private void StartWavFileDecoding()
    {
        string path = txtWavPath.Text.Trim();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "Please select a valid WAV file first.",
                "No File Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            btnBrowse.Focus();
            return;
        }

        var proto     = SelectedProtocol;
        _wavDecodeCts = new CancellationTokenSource();
        var cts       = _wavDecodeCts;

        SetDecodingState(true, $"Loading {IOPath.GetFileName(path)}…");
        AppendToLog($"Decoding: {IOPath.GetFileName(path)} [{proto.Name}]");

        _ = Task.Run(async () =>
        {
            try
            {
                await DecodeWavFileAsync(path, proto, cts.Token);
                Dispatcher.Invoke(() => { AppendToLog("Done."); CleanupWavDecode("Done"); });
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() => { AppendToLog("Stopped."); CleanupWavDecode("Stopped"); });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => { CleanupWavDecode("Error"); ShowError("Decode error", ex.Message); });
            }
        }, cts.Token);
    }

    private async Task DecodeWavFileAsync(string wavPath, IProtocol proto, CancellationToken ct)
    {
        float[] allSamples;
        int srcRate;

        using (var reader = new AudioFileReader(wavPath))
        {
            srcRate  = reader.WaveFormat.SampleRate;
            int ch   = reader.WaveFormat.Channels;
            var list = new List<float>();
            var chunk = new float[srcRate * ch];
            int read;

            while ((read = reader.Read(chunk, 0, chunk.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (ch == 1)
                    list.AddRange(chunk.AsSpan(0, read).ToArray());
                else
                    for (int i = 0; i < read; i += ch)
                    {
                        float sum = 0f;
                        for (int c = 0; c < ch; c++) sum += chunk[i + c];
                        list.Add(sum / ch);
                    }
            }
            allSamples = list.ToArray();
        }

        Dispatcher.Invoke(() => SetStatus("Resampling…"));
        float[] samples = srcRate == proto.SampleRate
            ? allSamples
            : new Resampler(srcRate, proto.SampleRate).Process(allSamples.AsSpan());

        int periodSamples = (int)(proto.PeriodDuration.TotalSeconds * proto.SampleRate);
        int totalPeriods  = Math.Max(1, (int)Math.Floor((double)samples.Length / periodSamples));
        Dispatcher.Invoke(() => SetStatus($"Decoding {totalPeriods} period(s)…"));

        // One decoder instance for the whole file — allows FT2 LLR accumulation across periods
        var decoder = proto.CreateDecoder();

        for (int p = 0; p < totalPeriods; p++)
        {
            ct.ThrowIfCancellationRequested();

            int start  = p * periodSamples;
            int length = Math.Min(periodSamples, samples.Length - start);
            float[] period = new float[periodSamples];
            samples.AsSpan(start, length).CopyTo(period);

            var results = await Task.Run(() => decoder.Decode(
                period, proto.DefaultFreqLow, proto.DefaultFreqHigh, $"P{p:D3}"), ct);

            var unique = results
                .GroupBy(r => r.Message, StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(r => r.Snr).First())
                .OrderByDescending(r => r.Snr).ToList();

            int pNum = p + 1;
            if (unique.Count > 0) PlayBeep(1200, 25);
            Dispatcher.Invoke(() =>
            {
                foreach (var r in unique) AppendToLog(r.ToString());
                SetStatus(unique.Count > 0
                    ? $"Period {pNum}/{totalPeriods}: {unique.Count} decode(s)"
                    : $"Period {pNum}/{totalPeriods}: no decodes");
            });
        }
    }

    private void StopWavFileDecoding() => _wavDecodeCts?.Cancel();

    private void CleanupWavDecode(string status)
    {
        _wavDecodeCts?.Dispose();
        _wavDecodeCts = null;
        SetDecodingState(false, status);
    }

    // ── Decode output ─────────────────────────────────────────────────────────

    private void OnPeriodDecoded(IReadOnlyList<DecodeResult> results, DateTimeOffset windowStart)
    {
        if (results.Count > 0) PlayBeep(1200, 25);
        Dispatcher.Invoke(() =>
        {
            foreach (var r in results) AppendToLog(r.ToString());
            SetStatus(results.Count > 0
                ? $"{windowStart:HH:mm:ss} UTC — {results.Count} decode(s)"
                : $"{windowStart:HH:mm:ss} UTC — no decodes");
        });
    }

    private void OnDecodeError(Exception ex)
        => Dispatcher.Invoke(() => SetStatus($"Decode error: {ex.Message}"));

    // ── Period beep timer ─────────────────────────────────────────────────────

    private void StartPeriodBeepTimer(IProtocol proto)
    {
        var now         = DateTimeOffset.UtcNow;
        var secsToNext  = (proto.NextPeriodStart(now) - now).TotalSeconds;
        _periodBeepTimer = new System.Threading.Timer(
            _ => PlayBeep(),
            null,
            TimeSpan.FromSeconds(secsToNext),
            proto.PeriodDuration);
    }

    private void StopPeriodBeepTimer()
    {
        _periodBeepTimer?.Dispose();
        _periodBeepTimer = null;
    }

    private static void PlayBeep(int freqHz = 700, int durationMs = 80)
    {
        _ = Task.Run(() =>
        {
            try
            {
                const int rate = 44100;
                int n    = rate * durationMs / 1000;
                int fade = rate / 100;
                var samples = new float[n];
                for (int i = 0; i < n; i++)
                {
                    float env = i < fade     ? (float)i / fade
                              : i > n - fade ? (float)(n - i) / fade
                              : 1f;
                    samples[i] = 0.28f * env * (float)Math.Sin(2 * Math.PI * freqHz * i / rate);
                }
                var bytes = new byte[n * 4];
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

                var provider = new BufferedWaveProvider(
                    WaveFormat.CreateIeeeFloatWaveFormat(rate, 1))
                    { DiscardOnBufferOverflow = true };
                provider.AddSamples(bytes, 0, bytes.Length);

                using var wo = new WaveOutEvent();
                wo.Init(provider);
                wo.Play();
                System.Threading.Thread.Sleep(durationMs + 50);
            }
            catch { /* never let a beep crash the app */ }
        });
    }

    // ── Audio devices ─────────────────────────────────────────────────────────

    private void PopulateAudioDevices()
    {
        cboAudioDevice.Items.Clear();
        int count = WaveIn.DeviceCount;
        for (int i = 0; i < count; i++)
            cboAudioDevice.Items.Add(WaveIn.GetCapabilities(i).ProductName);
        if (cboAudioDevice.Items.Count > 0)
            cboAudioDevice.SelectedIndex = 0;
        else
            cboAudioDevice.Items.Add("(no input devices found)");
    }

    // ── Log ───────────────────────────────────────────────────────────────────

    private void MenuClearLog_Click(object sender, RoutedEventArgs e)
        => rtbDecoded.Document.Blocks.Clear();

    private void AppendToLog(string line)
    {
        var doc = rtbDecoded.Document;

        if (doc.Blocks.Count >= MaxLogBlocks)
        {
            var remove = doc.Blocks.Take(TrimBlocks).ToList();
            foreach (var b in remove) doc.Blocks.Remove(b);
            _decodedSv?.ScrollToBottom();
        }

        var sv       = _decodedSv;
        double saved = sv?.VerticalOffset ?? 0;
        bool atBottom = sv == null || sv.ScrollableHeight < 1 || saved >= sv.ScrollableHeight - 5;

        doc.Blocks.Add(new Paragraph(new Run(line)) { Margin = new Thickness(0) });

        if (!atBottom && sv != null)
        {
            double captured = saved;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => sv.ScrollToVerticalOffset(captured)));
        }
    }
}
using HamDigiSharp.Abstractions;
using HamDigiSharp.Engine;
using HamDigiSharp.Models;
using HamDigiSharp.Protocols;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace HamDigiSharp.Demo;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _decodedSv = FindVisualChild<ScrollViewer>(rtbDecoded);
        rtbDecoded.Document.Blocks.Clear();

        // Populate mode selector from registry — popular FT modes first, then the rest
        var ordered = ProtocolRegistry.All.Values
            .OrderBy(p => p.Mode switch
            {
                DigitalMode.FT8      => 0,
                DigitalMode.FT4      => 1,
                DigitalMode.FT2      => 2,
                DigitalMode.SuperFox => 3,
                _ => 100 + (int)p.Mode,
            })
            .ToList();

        cboMode.ItemsSource      = ordered;
        cboMode.DisplayMemberPath = "Name";
        cboMode.SelectedItem     = ordered.First(p => p.Mode == DigitalMode.FT8);

        PopulateAudioDevices();
    }

    private DigitalMode SelectedMode => SelectedProtocol.Mode;

    /// <summary>Returns the protocol for the currently selected mode.</summary>
    private IProtocol SelectedProtocol =>
        (cboMode.SelectedItem as IProtocol) ?? ProtocolRegistry.Get(DigitalMode.FT8);

    private void CboMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var mc = SelectedProtocol.MessageConstraints;
        txtMessage.MaxLength = mc.MaxLength;
        txtMessage.ToolTip   = mc.FormatHint;
        UpdateTxPanel();
        ValidateMessageInput();
    }

    /// <summary>Shows or hides the transmit panel based on whether the selected protocol can encode.</summary>
    private void UpdateTxPanel()
    {
        bool canEncode = SelectedProtocol.CanEncode;
        lblMessage.IsEnabled  = canEncode;
        txtMessage.IsEnabled  = canEncode;
        btnTransmit.IsEnabled = canEncode;
        if (!canEncode)
            lblMessage.ToolTip = btnTransmit.ToolTip =
                $"{SelectedProtocol.Name} is receive-only — no encoder available.";
        else
        {
            lblMessage.ToolTip  = null;
            btnTransmit.ToolTip = null;
        }
    }

    private void TxtMessage_TextChanged(object sender, TextChangedEventArgs e)
        => ValidateMessageInput();

    private void ValidateMessageInput()
    {
        if (!IsLoaded) return;
        string msg   = txtMessage.Text;
        string? err  = string.IsNullOrEmpty(msg)
            ? null
            : SelectedProtocol.MessageConstraints.Validate(msg.ToUpperInvariant());
        btnTransmit.ToolTip = err;
        txtMessage.BorderBrush = err is null
            ? System.Windows.SystemColors.ControlDarkBrush
            : System.Windows.Media.Brushes.OrangeRed;
    }

    private void UpdateTransmitButton()
    {
        if (rbWavFile.IsChecked == true)
        {
            btnTransmit.Content = "💾 Encode & _Save WAV";
            AutomationProperties.SetName(btnTransmit, "Encode and Save WAV");
            AutomationProperties.SetHelpText(btnTransmit, "Encode the message and save it as a WAV file");
        }
        else
        {
            btnTransmit.Content = "🔊 Encode & _Play";
            AutomationProperties.SetName(btnTransmit, "Encode and Play");
            AutomationProperties.SetHelpText(btnTransmit, "Encode the message and play it through the sound card");
        }
    }

    private void SetDecodingState(bool decoding, string status)
    {
        btnStartStop.Content     = decoding ? "⏹ _Stop Decoding" : "▶ _Start Decoding";
        AutomationProperties.SetName(btnStartStop, decoding ? "Stop Decoding" : "Start Decoding");
        cboMode.IsEnabled        = !decoding;
        rbRealTime.IsEnabled     = !decoding;
        rbWavFile.IsEnabled      = !decoding;
        cboAudioDevice.IsEnabled = !decoding && rbRealTime.IsChecked == true;
        btnBrowse.IsEnabled      = !decoding && rbWavFile.IsChecked == true;
        SetStatus(status);
    }

    private void SetStatus(string text) => txbStatus.Text = text;

    private void ShowError(string title, string message)
    {
        SetStatus($"Error: {message}");
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T hit) return hit;
            var found = FindVisualChild<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    protected override void OnClosed(EventArgs e)
    {
        _txDelayCts?.Cancel();
        _txDelayCts?.Dispose();
        StopDecoding();
        _waveOut?.Dispose();
        base.OnClosed(e);
    }
}
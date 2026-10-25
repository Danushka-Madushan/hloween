using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace KioskOverlay
{
    public partial class ControlPanelWindow : Window
    {
        private readonly MainWindow _mainWindow;
        private readonly Esp32Service _espService;
        private string _currentIp = "192.168.4.1";

        public ControlPanelWindow(MainWindow mainWindow, Esp32Service espService)
        {
            InitializeComponent();
            _mainWindow = mainWindow;
            _espService = espService;

            // Initialize control values from MainWindow state
            DurationSlider.Value = _mainWindow.MinimumPopupDuration.TotalSeconds;
            TxtDurationValue.Text = $"{(int)DurationSlider.Value} seconds";
            ChkDemoTimer.IsChecked = _mainWindow.IsDemoTimerEnabled;

            // Subscribe to ESP32 service events
            _espService.StatusUpdated += EspService_StatusUpdated;
            _espService.LogMessage += (s, msg) => AppendLog(msg);

            // Subscribe to MainWindow logs
            _mainWindow.LogBroadcast += (s, msg) => AppendLog(msg);

            AppendLog("[CONTROL PANEL] Dashboard initialized and ready.");
        }

        private void EspService_StatusUpdated(object? sender, Esp32Status status)
        {
            Dispatcher.Invoke(() =>
            {
                if (status.IsConnected)
                {
                    StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x88)); // Neon Green
                    StatusBadge.Text = $"CONNECTED ({status.Mode})";
                    StatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x88));

                    _currentIp = status.IpAddress;
                    TxtIpAddress.Text = status.IpAddress;
                    TxtDistance.Text = $"{status.DistanceCm} cm";
                    TxtDoorState.Text = status.DoorOpen ? "🚪 OPEN" : "LATCHED";
                    TxtDoorState.Foreground = status.DoorOpen
                        ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)) // Red
                        : new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x88)); // Green

                    TxtTriggers.Text = status.TriggerCount.ToString();
                }
                else
                {
                    StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)); // Red
                    StatusBadge.Text = "WAITING FOR ESP32...";
                    StatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));

                    TxtDoorState.Text = "OFFLINE";
                    TxtDoorState.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x84, 0x96));
                }
            });
        }

        public void AppendLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
                TxtEventLog.AppendText(line + Environment.NewLine);
                LogScrollViewer.ScrollToEnd();
            });
        }

        private void DurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtDurationValue == null || _mainWindow == null) return;
            int seconds = (int)Math.Round(e.NewValue);
            TxtDurationValue.Text = $"{seconds} seconds";
            _mainWindow.MinimumPopupDuration = TimeSpan.FromSeconds(seconds);
        }

        private void ChkDemoTimer_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_mainWindow == null) return;
            bool enabled = ChkDemoTimer.IsChecked == true;
            _mainWindow.IsDemoTimerEnabled = enabled;
            AppendLog($"[DEMO TIMER] Automatic recurring timer {(enabled ? "ENABLED (every 15s)" : "DISABLED")}.");
        }

        private async void BtnManualTrigger_Click(object sender, RoutedEventArgs e)
        {
            AppendLog("[CONTROL PANEL] Manual trigger button clicked. Launching alert popup...");
            await _mainWindow.TriggerPopupAsync();
        }

        private void OpenWebUiButton_Click(object sender, RoutedEventArgs e)
        {
            string url = $"http://{_currentIp}";
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
                AppendLog($"[BROWSER] Opening ESP32 dashboard at {url}");
            }
            catch (Exception ex)
            {
                AppendLog($"[BROWSER ERROR] Could not open {url}: {ex.Message}");
            }
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            TxtEventLog.Clear();
        }

        protected override void OnClosed(EventArgs e)
        {
            _espService.Stop();
            Application.Current.Shutdown();
            base.OnClosed(e);
        }
    }
}
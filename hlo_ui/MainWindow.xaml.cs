using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KioskOverlay
{
    /// <summary>
    /// Custom WaveStream to provide seamless, gapless looping using NAudio.
    /// </summary>
    public class LoopStream : WaveStream
    {
        private readonly WaveStream _sourceStream;

        public LoopStream(WaveStream sourceStream)
        {
            _sourceStream = sourceStream;
            EnableLooping = true;
        }

        public bool EnableLooping { get; set; }

        public override WaveFormat WaveFormat => _sourceStream.WaveFormat;

        public override long Length => _sourceStream.Length;

        public override long Position
        {
            get => _sourceStream.Position;
            set => _sourceStream.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int totalBytesRead = 0;

            while (totalBytesRead < count)
            {
                int bytesRead = _sourceStream.Read(buffer, offset + totalBytesRead, count - totalBytesRead);
                if (bytesRead == 0)
                {
                    if (_sourceStream.Position == 0 || !EnableLooping)
                        break;
                    _sourceStream.Position = 0;
                }
                totalBytesRead += bytesRead;
            }

            return totalBytesRead;
        }
    }

    public partial class MainWindow : Window
    {
        // --- Win32 API to defeat PowerPoint Fullscreen TopMost lock ---
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_SHOWWINDOW = 0x0040;

        // --- Timers & Audio Players ---
        private readonly DispatcherTimer _triggerTimer = new DispatcherTimer();

        // NAudio Ambient Loop components
        private AudioFileReader? _ambientReader;
        private LoopStream? _ambientLoopStream;
        private VolumeSampleProvider? _ambientVolume;
        private WaveOut? _ambientOutput;

        // NAudio Alert components
        private AudioFileReader? _alertReader;
        private WaveOut? _alertOutput;

        // ESP32 Background Service & Operator Control Panel
        private readonly Esp32Service _espService = new Esp32Service();
        private ControlPanelWindow? _controlPanel;

        // Random generator and non-repeating state for alert voice selection
        private readonly Random _random = new Random();
        private int _lastAlertIndex = -1;

        // Configuration Properties (Controllable via Control Panel)
        public TimeSpan MinimumPopupDuration { get; set; } = TimeSpan.FromSeconds(14);
        public bool IsDemoTimerEnabled { get; set; } = false; // Controlled via Control Panel checkbox

        private readonly TimeSpan _triggerInterval = TimeSpan.FromSeconds(15); // Interval when demo timer is enabled
        private const float DefaultAmbientVolume = 0.6f;
        private const float DuckedAmbientVolume = 0.15f;

        private bool _isPopupActive = false;

        public event EventHandler<string>? LogBroadcast;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Log(string message)
        {
            Console.WriteLine(message);
            LogBroadcast?.Invoke(this, message);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Log("[KIOSK] Application started.");

            // 1. Initialize and start the ambient background sound loop
            SetupAmbientAudio();

            // 2. Hide fullscreen kiosk overlay to background initially
            HideToBackground();
            Log("[KIOSK] Hidden to background. Ambient sound playing.");

            // 3. Launch the dedicated Operator Control Panel Window
            _controlPanel = new ControlPanelWindow(this, _espService);
            _controlPanel.Show();
            Log("[KIOSK] Operator Control Panel launched.");

            // 4. Hook ESP32 live triggers
            _espService.DoorTriggered += async (s, triggerCount) =>
            {
                Log($"[DOOR TRIGGER] ESP32 Door Event #{triggerCount} received! Launching popup...");
                await Dispatcher.InvokeAsync(async () =>
                {
                    await TriggerPopupAsync();
                });
            };
            _espService.Start();

            // 5. Setup recurring demo timer (only triggers if IsDemoTimerEnabled is true)
            _triggerTimer.Interval = _triggerInterval;
            _triggerTimer.Tick += async (s, args) =>
            {
                if (IsDemoTimerEnabled && !_isPopupActive)
                {
                    Log($"[DEMO TIMER] Fired at {DateTime.Now:HH:mm:ss}! Popping up...");
                    await TriggerPopupAsync();
                }
            };
            _triggerTimer.Start();
        }

        private string? ResolveAssetPath(string fileName)
        {
            // 1. Check build output directory (bin/Debug/.../Assets/)
            string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", fileName);
            if (File.Exists(localPath)) return localPath;

            // 2. Fallback to project source directory (in case assets were added without rebuild)
            string projectPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Assets", fileName));
            if (File.Exists(projectPath)) return projectPath;

            return null;
        }

        private List<string> GetAlertFiles()
        {
            var files = new List<string>();
            var extensions = new[] { "*.wav", "*.mp3", "*.m4a", "*.aac" };

            // 1. Check build output directory (bin/Debug/.../Assets/alerts/)
            string localAlertsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "alerts");
            // 2. Fallback to project source directory
            string projectAlertsDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Assets", "alerts"));

            string targetDir = Directory.Exists(localAlertsDir) ? localAlertsDir :
                               Directory.Exists(projectAlertsDir) ? projectAlertsDir : "";

            if (!string.IsNullOrEmpty(targetDir))
            {
                foreach (var ext in extensions)
                {
                    files.AddRange(Directory.GetFiles(targetDir, ext));
                }
            }

            return files.Distinct().OrderBy(f => f).ToList();
        }

        private string? GetNextRandomAlertFile()
        {
            var alertFiles = GetAlertFiles();
            if (alertFiles.Count == 0) return null;
            if (alertFiles.Count == 1) return alertFiles[0];

            int index;
            do
            {
                index = _random.Next(alertFiles.Count);
            } while (index == _lastAlertIndex);

            _lastAlertIndex = index;
            return alertFiles[index];
        }

        private void SetupAmbientAudio()
        {
            string? bgPath = ResolveAssetPath("ambient_loop.mp3");

            if (bgPath != null)
            {
                try
                {
                    Log($"[AUDIO] Loading ambient loop from: {bgPath}");
                    _ambientReader = new AudioFileReader(bgPath);
                    _ambientLoopStream = new LoopStream(_ambientReader) { EnableLooping = true };
                    _ambientVolume = new VolumeSampleProvider(_ambientLoopStream.ToSampleProvider())
                    {
                        Volume = DefaultAmbientVolume
                    };

                    _ambientOutput = new WaveOut();
                    _ambientOutput.Init(_ambientVolume);
                    _ambientOutput.Play();

                    Log($"[AUDIO] Ambient sound successfully started! Format: {_ambientReader.WaveFormat}, Volume: {_ambientVolume.Volume}");
                }
                catch (Exception ex)
                {
                    Log($"[AUDIO ERROR] Failed to start ambient audio: {ex.Message}");
                }
            }
            else
            {
                Log($"[AUDIO WARNING] Missing Assets/ambient_loop.mp3! Searched in: {Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets")}");
            }
        }

        public async Task TriggerPopupAsync()
        {
            if (_isPopupActive) return;
            _isPopupActive = true;

            try
            {
                string? alertPath = GetNextRandomAlertFile();
                bool hasAlert = alertPath != null;
                TimeSpan duration = MinimumPopupDuration;

                // 1. Duck background music volume if alert voice is present
                if (hasAlert && _ambientVolume != null)
                {
                    Log($"[AUDIO] Ducking ambient music to {DuckedAmbientVolume} for alert voice...");
                    _ambientVolume.Volume = DuckedAmbientVolume;
                }
                else
                {
                    Log("[AUDIO INFO] No alert voice found in Assets/alerts - ambient sound continues playing normally.");
                }

                // 2. Show window and force it above Fullscreen PowerPoint
                this.Show();
                this.WindowState = WindowState.Maximized;
                this.Topmost = true;

                IntPtr hWnd = new WindowInteropHelper(this).Handle;
                SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                SetForegroundWindow(hWnd);

                // 3. Play alert sound if available and dynamically determine popup duration
                if (hasAlert && alertPath != null)
                {
                    try
                    {
                        StopAlertAudio();
                        _alertReader = new AudioFileReader(alertPath);
                        _alertOutput = new WaveOut();
                        _alertOutput.Init(_alertReader);
                        _alertOutput.Play();

                        // Dynamic popup duration: audio length + 800ms natural padding (minimum MinimumPopupDuration)
                        TimeSpan audioLength = _alertReader.TotalTime;
                        duration = audioLength + TimeSpan.FromMilliseconds(800);
                        if (duration < MinimumPopupDuration) duration = MinimumPopupDuration;

                        Log($"[ALERT] Playing random alert: {Path.GetFileName(alertPath)} (Audio: {audioLength:mm\\:ss\\.ff}, Total Display: {duration:mm\\:ss\\.ff})");
                    }
                    catch (Exception ex)
                    {
                        Log($"[AUDIO ERROR] Could not play alert voice: {ex.Message}");
                    }
                }

                // 4. Keep popup on screen for the duration of the alert voice / minimum duration
                await Task.Delay(duration);

                // 5. Clean up alert audio and return to background
                StopAlertAudio();
                HideToBackground();

                // 6. Restore background loop volume
                if (_ambientVolume != null)
                {
                    _ambientVolume.Volume = DefaultAmbientVolume;
                    Log($"[AUDIO] Restored ambient volume to {DefaultAmbientVolume}.");
                }
            }
            finally
            {
                _isPopupActive = false;
            }
        }

        private void StopAlertAudio()
        {
            try
            {
                if (_alertOutput != null)
                {
                    _alertOutput.Stop();
                    _alertOutput.Dispose();
                    _alertOutput = null;
                }
                if (_alertReader != null)
                {
                    _alertReader.Dispose();
                    _alertReader = null;
                }
            }
            catch (Exception ex)
            {
                Log($"[AUDIO WARNING] Error disposing alert player: {ex.Message}");
            }
        }

        private void HideToBackground()
        {
            this.Hide();
        }

        protected override void OnClosed(EventArgs e)
        {
            _triggerTimer.Stop();
            _espService.Dispose();
            StopAlertAudio();

            try
            {
                _ambientOutput?.Stop();
                _ambientOutput?.Dispose();
                _ambientLoopStream?.Dispose();
                _ambientReader?.Dispose();
            }
            catch { }

            base.OnClosed(e);
        }
    }
}
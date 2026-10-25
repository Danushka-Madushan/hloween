using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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
                    if (!EnableLooping)
                        break;

                    if (_sourceStream.Position == 0)
                    {
                        // Genuine end of readable data; prevent infinite spin
                        break;
                    }
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

        // --- Timers & Concurrency Locks ---
        private readonly DispatcherTimer _triggerTimer = new DispatcherTimer();
        private readonly SemaphoreSlim _popupLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource? _currentPopupCts;

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

        // Operational State
        private volatile bool _isAppRunning = true;
        private bool _isShuttingDown = false;

        // Configuration Properties (Controllable via Control Panel)
        public TimeSpan TriggerDelay { get; set; } = TimeSpan.FromSeconds(2.5);
        public TimeSpan MinimumPopupDuration { get; set; } = TimeSpan.FromSeconds(14);
        public bool IsDemoTimerEnabled { get; set; } = false; // Controlled via Control Panel checkbox

        private readonly TimeSpan _triggerInterval = TimeSpan.FromSeconds(15);
        private const float DefaultAmbientVolume = 0.6f;
        private const float DuckedAmbientVolume = 0.15f;

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
            Log("[KIOSK] Application starting...");

            // 1. Initialize and start perpetual ambient background sound loop
            SetupAmbientAudio();

            // 2. Hide fullscreen kiosk overlay to background initially
            HideToBackground();
            Log("[KIOSK] Hidden to background. Ambient sound playing.");

            // 3. Launch dedicated Operator Control Panel Window
            _controlPanel = new ControlPanelWindow(this, _espService);
            _controlPanel.Show();
            Log("[KIOSK] Operator Control Panel launched.");

            // 4. Hook ESP32 live triggers
            _espService.LogMessage += (s, msg) => Console.WriteLine(msg);
            _espService.DoorTriggered += async (s, triggerCount) =>
            {
                Log($"[DOOR TRIGGER] ESP32 Door Event #{triggerCount} received! Launching popup with configured delay...");
                await Dispatcher.InvokeAsync(async () =>
                {
                    await TriggerPopupAsync(applyDelay: true);
                });
            };
            _espService.Start();

            // 5. Setup recurring demo timer
            _triggerTimer.Interval = _triggerInterval;
            _triggerTimer.Tick += async (s, args) =>
            {
                if (IsDemoTimerEnabled)
                {
                    Log($"[DEMO TIMER] Fired at {DateTime.Now:HH:mm:ss}! Popping up...");
                    await TriggerPopupAsync(applyDelay: true);
                }
            };
            _triggerTimer.Start();
        }

        public void DismissActivePopup()
        {
            Log("[POPUP] Dismissing active popup / aborting pending delay countdown...");
            try
            {
                _currentPopupCts?.Cancel();
            }
            catch (ObjectDisposedException) { }
            HideToBackground();
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            // Escape: Dismiss active popup immediately
            if (e.Key == Key.Escape)
            {
                DismissActivePopup();
            }
            // Ctrl + Q: Clean emergency termination
            else if (e.Key == Key.Q && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                Log("[KEYBOARD] Ctrl+Q shortcut detected on kiosk screen. Shutting down...");
                ShutdownApplication();
            }
        }

        private string? ResolveAssetPath(string fileName)
        {
            // 1. Check build output directory (bin/Debug/.../Assets/)
            string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", fileName);
            if (File.Exists(localPath)) return localPath;

            // 2. Fallback to project source directory
            string projectPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Assets", fileName));
            if (File.Exists(projectPath)) return projectPath;

            return null;
        }

        private List<string> GetAlertFiles()
        {
            var files = new List<string>();
            var extensions = new[] { "*.wav", "*.mp3", "*.m4a", "*.aac" };

            string localAlertsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "alerts");
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
                    StopAmbientAudio();

                    Log($"[AUDIO] Loading ambient loop from: {bgPath}");
                    _ambientReader = new AudioFileReader(bgPath);
                    _ambientLoopStream = new LoopStream(_ambientReader) { EnableLooping = true };
                    _ambientVolume = new VolumeSampleProvider(_ambientLoopStream.ToSampleProvider())
                    {
                        Volume = DefaultAmbientVolume
                    };

                    _ambientOutput = new WaveOut();
                    _ambientOutput.Init(_ambientVolume);

                    // Perpetual looping guarantee & audio watchdog:
                    // If audio driver resets, HDMI monitor sleeps, or stream stops, automatically recover!
                    _ambientOutput.PlaybackStopped += (s, e) =>
                    {
                        if (_isAppRunning && !_isShuttingDown)
                        {
                            Log($"[AUDIO WATCHDOG] Ambient playback stopped unexpectedly ({e.Exception?.Message ?? "EOF/device reset"}). Auto-recovering ambient loop in 300ms...");
                            Task.Run(async () =>
                            {
                                await Task.Delay(300);
                                if (_isAppRunning && !_isShuttingDown)
                                {
                                    Dispatcher.Invoke(() => RecoverAmbientAudio());
                                }
                            });
                        }
                    };

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

        private void RecoverAmbientAudio()
        {
            try
            {
                Log("[AUDIO WATCHDOG] Reconstructing ambient audio pipeline...");
                SetupAmbientAudio();
            }
            catch (Exception ex)
            {
                Log($"[AUDIO WATCHDOG ERROR] Ambient audio recovery failed: {ex.Message}");
            }
        }

        public async Task TriggerPopupAsync(bool applyDelay = true)
        {
            // Concurrency lock: Prevent overlapping popups under high-frequency triggers
            if (!await _popupLock.WaitAsync(0))
            {
                Log("[TRIGGER] Popup sequence already armed/active. Skipping overlapping trigger.");
                return;
            }

            _currentPopupCts = new CancellationTokenSource();
            var ct = _currentPopupCts.Token;

            try
            {
                // Optional pre-activation delay (e.g. 2.5s) to allow visitor to walk through door
                if (applyDelay && TriggerDelay > TimeSpan.Zero)
                {
                    Log($"[TRIGGER DELAY] Door opened. Waiting {TriggerDelay.TotalSeconds:F1}s for visitor to enter before launching alert...");
                    try
                    {
                        await Task.Delay(TriggerDelay, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        Log("[TRIGGER DELAY] Trigger delay canceled before popup display.");
                        return;
                    }
                }

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
                        if (_ambientVolume != null)
                        {
                            _ambientVolume.Volume = DefaultAmbientVolume;
                            Log($"[AUDIO] Restored ambient volume to {DefaultAmbientVolume} after alert failure.");
                        }
                    }
                }

                // 4. Keep popup on screen for the duration of the alert voice / minimum duration
                try
                {
                    await Task.Delay(duration, ct);
                }
                catch (OperationCanceledException)
                {
                    Log("[TRIGGER] Active popup dismissed early by user.");
                }

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
                if (_ambientVolume != null && _ambientVolume.Volume != DefaultAmbientVolume)
                {
                    _ambientVolume.Volume = DefaultAmbientVolume;
                }
                _currentPopupCts?.Dispose();
                _currentPopupCts = null;
                _popupLock.Release();
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

        private void StopAmbientAudio()
        {
            try
            {
                if (_ambientOutput != null)
                {
                    _ambientOutput.Stop();
                    _ambientOutput.Dispose();
                    _ambientOutput = null;
                }
                if (_ambientLoopStream != null)
                {
                    _ambientLoopStream.Dispose();
                    _ambientLoopStream = null;
                }
                if (_ambientReader != null)
                {
                    _ambientReader.Dispose();
                    _ambientReader = null;
                }
            }
            catch { }
        }

        private void HideToBackground()
        {
            this.Hide();
        }

        public void ShutdownApplication()
        {
            if (_isShuttingDown) return;
            _isShuttingDown = true;
            _isAppRunning = false;

            Log("[SHUTDOWN] Performing clean termination of all services and audio streams...");

            try { _triggerTimer.Stop(); } catch { }
            try { _espService.Dispose(); } catch { }
            try { _currentPopupCts?.Cancel(); } catch { }

            StopAlertAudio();
            StopAmbientAudio();

            try
            {
                _controlPanel?.Close();
            }
            catch { }

            try
            {
                this.Close();
            }
            catch { }

            Log("[SHUTDOWN] Complete. Exiting.");
            Application.Current.Shutdown();

            // Background fallback exit to guarantee zero zombie threads
            Task.Run(async () =>
            {
                await Task.Delay(600);
                Environment.Exit(0);
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            ShutdownApplication();
            base.OnClosed(e);
        }
    }
}

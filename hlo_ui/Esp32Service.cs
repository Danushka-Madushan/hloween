using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace KioskOverlay
{
    public class Esp32Status
    {
        public bool IsConnected { get; set; } = false;
        public string IpAddress { get; set; } = "Searching...";
        public string Mode { get; set; } = "Unknown";
        public long DistanceCm { get; set; } = 0;
        public long TriggerCount { get; set; } = 0;
        public bool DoorOpen { get; set; } = false;
        public DateTime LastSeen { get; set; } = DateTime.MinValue;
    }

    public class Esp32Service : IDisposable
    {
        private const int UdpPort = 4210;
        private readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(1.5) };
        private CancellationTokenSource? _cts;

        private Esp32Status _status = new Esp32Status();
        private long _lastTriggerCount = -1;
        private DateTime _lastTriggerTime = DateTime.MinValue;
        private readonly TimeSpan _triggerCooldown = TimeSpan.FromSeconds(2);

        public event EventHandler<Esp32Status>? StatusUpdated;
        public event EventHandler<long>? DoorTriggered;
        public event EventHandler<string>? LogMessage;

        public Esp32Status CurrentStatus => _status;

        public void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();

            LogMessage?.Invoke(this, $"[ESP32 SERVICE] Listening on UDP Port {UdpPort} for ESP32 broadcasts...");

            // 1. Run UDP Listener
            _ = Task.Run(() => ListenUdpAsync(_cts.Token));

            // 2. Run HTTP Poller & Heartbeat Monitor
            _ = Task.Run(() => PollHttpAndMonitorHeartbeatAsync(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        public void ManualTrigger()
        {
            long simulatedCount = _status.TriggerCount + 1;
            _status.TriggerCount = simulatedCount;
            LogMessage?.Invoke(this, $"[MANUAL] Operator fired manual trigger #{simulatedCount}.");
            DoorTriggered?.Invoke(this, simulatedCount);
        }

        private async Task ListenUdpAsync(CancellationToken ct)
        {
            UdpClient? udpClient = null;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    udpClient = new UdpClient();
                    udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, UdpPort));

                    while (!ct.IsCancellationRequested)
                    {
                        var result = await udpClient.ReceiveAsync(ct);
                        string message = Encoding.UTF8.GetString(result.Buffer).Trim();

                        if (string.IsNullOrEmpty(message)) continue;

                        ProcessIncomingMessage(message, result.RemoteEndPoint.Address.ToString());
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke(this, $"[UDP ERROR] {ex.Message}. Retrying listener in 3 seconds...");
                    udpClient?.Dispose();
                    udpClient = null;
                    try { await Task.Delay(3000, ct); } catch { break; }
                }
                finally
                {
                    udpClient?.Dispose();
                }
            }
        }

        private void ProcessIncomingMessage(string message, string senderIp)
        {
            // 1. Immediate Trigger Packet: [ESP32_TRIGGER] #1 | Dist: 45cm | IP: http://192.168.1.50 | Mode: DEV_STA
            if (message.StartsWith("[ESP32_TRIGGER]"))
            {
                LogMessage?.Invoke(this, $"[UDP TRIGGER] {message}");
                ParseAndUpdateStatus(message, senderIp);
                CheckAndFireTrigger();
            }
            // 2. Periodic Status Beacon: [ESP32_BEACON] IP: http://192.168.1.50 | Dist: 12cm | Triggers: 1 | Mode: DEV_STA
            else if (message.StartsWith("[ESP32_BEACON]"))
            {
                ParseAndUpdateStatus(message, senderIp);
            }
        }

        private void ParseAndUpdateStatus(string message, string senderIp)
        {
            // Extract IP
            var ipMatch = Regex.Match(message, @"IP:\s*http://([0-9a-zA-Z\.\-]+)");
            string ip = ipMatch.Success ? ipMatch.Groups[1].Value : senderIp;

            // Extract Distance
            var distMatch = Regex.Match(message, @"Dist:\s*(\d+)");
            long dist = distMatch.Success && long.TryParse(distMatch.Groups[1].Value, out long d) ? d : _status.DistanceCm;

            // Extract Triggers
            var trigMatch = Regex.Match(message, @"(?:Triggers:|\#)\s*(\d+)");
            long triggers = trigMatch.Success && long.TryParse(trigMatch.Groups[1].Value, out long t) ? t : _status.TriggerCount;

            // Extract Mode
            var modeMatch = Regex.Match(message, @"Mode:\s*([A-Za-z0-9_]+)");
            string mode = modeMatch.Success ? modeMatch.Groups[1].Value : _status.Mode;

            bool wasConnected = _status.IsConnected;

            _status.IsConnected = true;
            _status.IpAddress = ip;
            _status.DistanceCm = dist;
            _status.Mode = mode;
            _status.DoorOpen = dist > 22; // Matches ESP32 threshold (>22cm is door open)
            _status.LastSeen = DateTime.Now;

            if (!wasConnected)
            {
                LogMessage?.Invoke(this, $"[ESP32 ONLINE] Connected to ESP32 at http://{_status.IpAddress} (Mode: {_status.Mode})");
            }

            // Check if trigger count increased in beacon
            if (_lastTriggerCount >= 0 && triggers > _lastTriggerCount)
            {
                _status.TriggerCount = triggers;
                CheckAndFireTrigger();
            }
            else
            {
                _status.TriggerCount = triggers;
                if (_lastTriggerCount < 0) _lastTriggerCount = triggers;
            }

            StatusUpdated?.Invoke(this, _status);
        }

        private void CheckAndFireTrigger()
        {
            if (DateTime.Now - _lastTriggerTime >= _triggerCooldown)
            {
                _lastTriggerTime = DateTime.Now;
                _lastTriggerCount = _status.TriggerCount;
                LogMessage?.Invoke(this, $"[DOOR EVENT] Triggering kiosk alert sequence! (Trigger #{_status.TriggerCount})");
                DoorTriggered?.Invoke(this, _status.TriggerCount);
            }
        }

        private async Task PollHttpAndMonitorHeartbeatAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // 1. Check Heartbeat
                    if (_status.IsConnected && DateTime.Now - _status.LastSeen > TimeSpan.FromSeconds(6))
                    {
                        _status.IsConnected = false;
                        LogMessage?.Invoke(this, "[ESP32 OFFLINE] Lost connection (no beacon or HTTP response for 6s).");
                        StatusUpdated?.Invoke(this, _status);
                    }

                    // 2. Poll HTTP /status endpoint if we have an active IP or fallback
                    string targetIp = _status.IsConnected && !string.IsNullOrEmpty(_status.IpAddress) && _status.IpAddress != "Searching..."
                        ? _status.IpAddress
                        : "192.168.4.1"; // Standalone AP fallback

                    string url = $"http://{targetIp}/status";

                    try
                    {
                        var response = await _httpClient.GetStringAsync(url, ct);
                        if (!string.IsNullOrEmpty(response))
                        {
                            using var doc = JsonDocument.Parse(response);
                            var root = doc.RootElement;

                            long triggers = root.TryGetProperty("triggers", out var trProp) ? trProp.GetInt64() : _status.TriggerCount;
                            long dist = root.TryGetProperty("distance", out var distProp) ? distProp.GetInt64() : _status.DistanceCm;
                            bool doorOpen = root.TryGetProperty("doorOpen", out var doorProp) && doorProp.GetBoolean();
                            string ip = root.TryGetProperty("ip", out var ipProp) ? ipProp.GetString() ?? targetIp : targetIp;
                            bool isDev = root.TryGetProperty("isDev", out var devProp) && devProp.GetBoolean();

                            bool wasConnected = _status.IsConnected;
                            _status.IsConnected = true;
                            _status.IpAddress = ip;
                            _status.DistanceCm = dist;
                            _status.DoorOpen = doorOpen;
                            _status.Mode = isDev ? "DEV_STA" : "PROD_AP";
                            _status.LastSeen = DateTime.Now;

                            if (!wasConnected)
                            {
                                LogMessage?.Invoke(this, $"[ESP32 ONLINE] HTTP connection established: http://{_status.IpAddress}");
                            }

                            if (_lastTriggerCount >= 0 && triggers > _lastTriggerCount)
                            {
                                _status.TriggerCount = triggers;
                                CheckAndFireTrigger();
                            }
                            else
                            {
                                _status.TriggerCount = triggers;
                                if (_lastTriggerCount < 0) _lastTriggerCount = triggers;
                            }

                            StatusUpdated?.Invoke(this, _status);
                        }
                    }
                    catch
                    {
                        // Ignore individual HTTP poll timeouts; UDP is primary
                    }

                    await Task.Delay(400, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke(this, $"[SERVICE ERROR] {ex.Message}");
                    try { await Task.Delay(1000, ct); } catch { break; }
                }
            }
        }

        public void Dispose()
        {
            Stop();
            _httpClient.Dispose();
        }
    }
}
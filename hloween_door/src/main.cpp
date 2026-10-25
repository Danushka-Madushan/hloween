#include <Arduino.h>
#include <WiFi.h>
#include <WebServer.h>
#include <ESPmDNS.h>
#include <WiFiUdp.h>
#include <esp_wifi.h>
#include <esp_task_wdt.h>

// =========================================================================
// 1. PURE BUILT-IN AP CONFIGURATION (Zero Router Dependencies)
// =========================================================================
// Connect the Windows Kiosk Wi-Fi to this network:
const char* AP_SSID = "Halloween_Door";
const char* AP_PASS = "spooky2026"; // Must be at least 8 characters

// Static IP is permanently http://192.168.4.1
IPAddress local_IP(192, 168, 4, 1);
IPAddress gateway(192, 168, 4, 1);
IPAddress subnet(255, 255, 255, 0);

WebServer server(80);
WiFiUDP udp;

// =========================================================================
// 2. HARDWARE PINS (30-Pin ESP32 DevKit)
// =========================================================================
const int LED_PIN  = 2;  // Onboard Blue LED (Solid = AP Ready, Blinks on Trigger)
const int TRIG_PIN = 26; // HC-SR04 TRIG -> D27
const int ECHO_PIN = 27; // HC-SR04 ECHO -> D26 (Use 1k + 2k divider if possible!)

// =========================================================================
// 3. DOOR DETECTION TUNING
// =========================================================================
bool doorClosed = true;
unsigned long lastTriggerTime = 0;
unsigned long triggerCount = 0;
long currentDistance = 5;

const int DOOR_OPEN_THRESHOLD   = 22;   // Door is considered OPEN when distance > 22 cm
const int DOOR_CLOSED_THRESHOLD = 14;   // Door is considered CLOSED when distance < 14 cm
const unsigned long COOLDOWN_MS = 6000; // 6-second cooldown between triggers
const int CONFIRM_SAMPLES       = 2;    // Requires 2 consecutive open reads (~150ms)

int openConfirmCount = 0;
int closeConfirmCount = 0;

// Single ultrasonic reading with HC-SR04 stuck-echo recovery
long readSinglePingCm() {
  digitalWrite(TRIG_PIN, LOW);
  delayMicroseconds(4);
  digitalWrite(TRIG_PIN, HIGH);
  delayMicroseconds(15); // Strong 15us pulse
  digitalWrite(TRIG_PIN, LOW);

  // 18ms timeout (~3m max) keeps CPU & WebServer responsive
  long duration = pulseIn(ECHO_PIN, HIGH, 18000);

  // Anti-Lockup Recovery: If sensor misses echo or latches HIGH, reset ECHO line
  if (duration == 0) {
    if (digitalRead(ECHO_PIN) == HIGH) {
      pinMode(ECHO_PIN, OUTPUT);
      digitalWrite(ECHO_PIN, LOW);
      delayMicroseconds(500);
      pinMode(ECHO_PIN, INPUT);
    }
    return 300; // Open space / no echo = Door is wide open (300 cm)
  }

  long cm = duration * 0.034 / 2;
  if (cm < 2 || cm > 300) return 300;
  return cm;
}

// Median-of-3 filter: Ignores single acoustic glitches completely
long getReliableDistanceCm() {
  long a = readSinglePingCm();
  delay(6);
  long b = readSinglePingCm();
  delay(6);
  long c = readSinglePingCm();

  if ((a <= b && b <= c) || (c <= b && b <= a)) return b;
  if ((b <= a && a <= c) || (c <= a && a <= b)) return a;
  return c;
}

void addCorsHeaders() {
  server.sendHeader("Access-Control-Allow-Origin", "*");
  server.sendHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
  server.sendHeader("Access-Control-Allow-Headers", "*");
  server.sendHeader("Connection", "close"); // Prevents socket exhaustion over 14 days
}

void startPureAccessPoint() {
  digitalWrite(LED_PIN, LOW);

  // Disconnect any old station configs and lock into Pure AP Mode
  WiFi.disconnect(true);
  WiFi.mode(WIFI_AP);

  // Configure permanent static IP (192.168.4.1)
  WiFi.softAPConfig(local_IP, gateway, subnet);

  // Start AP on Channel 1, Visible SSID, Max 4 connections
  WiFi.softAP(AP_SSID, AP_PASS, 1, 0, 4);

  // Set moderate TX power (11dBm) -> Zero overheating, easily covers 6m to Kiosk
  WiFi.setTxPower(WIFI_POWER_11dBm);

  if (MDNS.begin("halloween")) {
    Serial.println("[MDNS_READY] http://halloween.local");
  }

  digitalWrite(LED_PIN, HIGH); // Solid Blue LED = AP Running!
  Serial.println("\n==========================================");
  Serial.println("[AP_ONLINE] SSID: " + String(AP_SSID));
  Serial.println("[ESP32_IP]  http://" + WiFi.softAPIP().toString());
  Serial.println("==========================================\n");
}

void setup() {
  // 1. Lock CPU clock at 80MHz (Coolest thermal profile that supports Wi-Fi)
  setCpuFrequencyMhz(80);

  Serial.begin(115200);
  pinMode(LED_PIN, OUTPUT);
  pinMode(TRIG_PIN, OUTPUT);
  pinMode(ECHO_PIN, INPUT);

  startPureAccessPoint();

  // 2. Web Dashboard (Visit http://192.168.4.1 in browser to test/monitor)
  server.on("/", HTTP_GET, []() {
    addCorsHeaders();
    String html = "<!DOCTYPE html><html><head><meta name='viewport' content='width=device-width,initial-scale=1'>";
    html += "<style>body{background:#111;color:#eee;font-family:sans-serif;text-align:center;padding:30px;}";
    html += ".box{background:#1c1c22;padding:25px;border-radius:12px;display:inline-block;border:2px solid #ff5500;}";
    html += "button{background:#ff5500;color:#fff;border:none;padding:12px 24px;font-size:16px;border-radius:6px;cursor:pointer;margin-top:15px;font-weight:bold;}</style></head><body>";
    html += "<div class='box'><h1>ESP32 Pure-AP Door Node</h1>";
    html += "<p><b>Permanent IP:</b> http://192.168.4.1 | <b>CPU:</b> " + String(getCpuFrequencyMhz()) + " MHz</p>";
    html += "<p><b>Uptime:</b> <span id='u'>0</span> mins | <b>Connected Kiosks:</b> <span id='c'>0</span></p>";
    html += "<p style='font-size:24px;color:#00ff88;'><b>Distance:</b> <span id='d'>" + String(currentDistance) + "</span> cm</p>";
    html += "<p><b>Door State:</b> <span id='s'>" + String(doorClosed ? "CLOSED" : "OPEN") + "</span></p>";
    html += "<p><b>Total Triggers:</b> <span id='t'>" + String(triggerCount) + "</span></p>";
    html += "<button onclick=\"fetch('/test')\">Fire Test Trigger</button></div>";
    html += "<script>setInterval(()=>{fetch('/status').then(r=>r.json()).then(d=>{";
    html += "document.getElementById('d').innerText=d.distance;";
    html += "document.getElementById('s').innerText=d.doorOpen?'OPEN':'CLOSED';";
    html += "document.getElementById('t').innerText=d.triggers;";
    html += "document.getElementById('u').innerText=d.uptimeMin;";
    html += "document.getElementById('c').innerText=d.clients;});},400);</script></body></html>";
    server.send(200, "text/html", html);
  });

  // 3. Fast JSON Endpoint Polled by Kiosk (http://192.168.4.1/status)
  server.on("/status", HTTP_GET, []() {
    addCorsHeaders();
    String json = "{\"triggers\":" + String(triggerCount) +
                  ",\"distance\":" + String(currentDistance) +
                  ",\"doorOpen\":" + String(!doorClosed ? "true" : "false") +
                  ",\"uptimeMin\":" + String(millis() / 60000) +
                  ",\"clients\":" + String(WiFi.softAPgetStationNum()) + "}";
    server.send(200, "application/json", json);
  });

  // 4. Manual Test Endpoint
  server.on("/test", HTTP_GET, []() {
    addCorsHeaders();
    triggerCount++;
    lastTriggerTime = millis();
    Serial.printf("[TEST_TRIGGER] #%lu\n", triggerCount);
    server.send(200, "text/plain", "Triggered!");
  });

  server.begin();

  // 5. Enable 15-Second Hardware Watchdog (Auto-reboots if MCU ever freezes over 14 days)
  #if ESP_IDF_VERSION >= ESP_IDF_VERSION_VAL(5, 0, 0)
    esp_task_wdt_config_t twdt_config = {
        .timeout_ms = 15000,
        .idle_core_mask = (1 << 0),
        .trigger_panic = true,
    };
    esp_task_wdt_reconfigure(&twdt_config);
    esp_task_wdt_add(NULL);
  #else
    esp_task_wdt_init(15, true);
    esp_task_wdt_add(NULL);
  #endif
}

void loop() {
  esp_task_wdt_reset(); // Feed hardware watchdog
  server.handleClient();

  unsigned long now = millis();

  // 1. Poll Ultrasonic Sensor every 75ms
  static unsigned long lastSensorRead = 0;
  if (now - lastSensorRead >= 75) {
    lastSensorRead = now;
    currentDistance = getReliableDistanceCm();

    if (currentDistance > DOOR_OPEN_THRESHOLD) {
      openConfirmCount++;
      closeConfirmCount = 0;
    } else if (currentDistance < DOOR_CLOSED_THRESHOLD) {
      closeConfirmCount++;
      openConfirmCount = 0;
    }

    // Door Opened! (Confirmed by 2 consecutive readings)
    if (openConfirmCount >= CONFIRM_SAMPLES && doorClosed && (now - lastTriggerTime > COOLDOWN_MS)) {
      doorClosed = false;
      openConfirmCount = 0;
      lastTriggerTime = now;
      triggerCount++;

      // Quick blink on the blue LED so you can visually see the trigger at the door
      digitalWrite(LED_PIN, LOW);
      delay(35);
      digitalWrite(LED_PIN, HIGH);

      Serial.printf("[DOOR_OPEN] Trigger #%lu | Dist: %ld cm\n", triggerCount, currentDistance);
    }

    // Door Closed! Re-arm after 2.5 seconds
    if (closeConfirmCount >= 3 && !doorClosed && (now - lastTriggerTime > 2500)) {
      doorClosed = true;
      closeConfirmCount = 0;
      Serial.printf("[DOOR_CLOSED] Re-armed | Dist: %ld cm\n", currentDistance);
    }
  }

  // 2. Serial & UDP Status Beacon every 3 seconds
  static unsigned long lastBeacon = 0;
  if (now - lastBeacon >= 3000) {
    lastBeacon = now;
    String beacon = "[ESP32_BEACON] IP: http://192.168.4.1 | Dist: " + String(currentDistance) +
                    "cm | Triggers: " + String(triggerCount) +
                    " | Clients: " + String(WiFi.softAPgetStationNum());
    Serial.println(beacon);

    // Broadcast on UDP 4210 for your .NET CLI logger
    udp.beginPacket(IPAddress(192, 168, 4, 255), 4210);
    udp.print(beacon);
    udp.endPacket();
  }

  // 3. Self-Healing RAM Check: If heap ever drops below 40KB over 2 weeks, clean restart while door is shut
  if (ESP.getFreeHeap() < 40000 && doorClosed) {
    ESP.restart();
  }

  // 4. Let CPU rest 10ms per loop -> Keeps ESP32 cool to the touch!
  delay(10);
}

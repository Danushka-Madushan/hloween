#include <Arduino.h>
#include <WiFi.h>
#include <WebServer.h>
#include <ESPmDNS.h>
#include <WiFiUdp.h>
#include <esp_wifi.h>
#include <esp_task_wdt.h>

// =========================================================================
// 1. NETWORK CONFIGURATION (Dev Station Mode vs Pure Built-In AP)
// =========================================================================
// Set isDev to true to connect to your home Wi-Fi during development.
// Set isDev to false for the standalone Halloween deployment (AP mode).
const bool isDev = true;

// --- Dev Mode Settings (Station Mode - Connects to Home Wi-Fi) ---
const char* DEV_SSID = "Dialog 4G 822";     // Change to your home Wi-Fi SSID
const char* DEV_PASS = "3058BAd5"; // Change to your home Wi-Fi password

// --- Production Mode Settings (Standalone Access Point) ---
const char* AP_SSID = "Halloween_Door";
const char* AP_PASS = "spooky2026"; // Must be at least 8 characters

// Static IP for standalone AP mode: http://192.168.4.1
IPAddress local_IP(192, 168, 4, 1);
IPAddress gateway(192, 168, 4, 1);
IPAddress subnet(255, 255, 255, 0);

WebServer server(80);
WiFiUDP udp;

// =========================================================================
// 2. HARDWARE PINS (30-Pin ESP32 DevKit)
// =========================================================================
const int LED_PIN  = 2;  // Onboard Blue LED (Solid = Ready, Blinks on Trigger / Connecting)
const int TRIG_PIN = 26; // HC-SR04 TRIG -> D26
const int ECHO_PIN = 27; // HC-SR04 ECHO -> D27

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

IPAddress getActiveIP() {
  return isDev ? WiFi.localIP() : local_IP;
}

void broadcastTrigger() {
  String activeIP = getActiveIP().toString();
  String triggerMsg = "[ESP32_TRIGGER] #" + String(triggerCount) + 
                      " | Dist: " + String(currentDistance) + "cm" +
                      " | IP: http://" + activeIP +
                      " | Mode: " + String(isDev ? "DEV_STA" : "PROD_AP");
  IPAddress broadcastIP = isDev ? IPAddress(255, 255, 255, 255) : IPAddress(192, 168, 4, 255);
  udp.beginPacket(broadcastIP, 4210);
  udp.print(triggerMsg);
  udp.endPacket();
}

void startDevStation() {
  digitalWrite(LED_PIN, LOW);

  WiFi.disconnect(true);
  WiFi.mode(WIFI_STA);
  WiFi.begin(DEV_SSID, DEV_PASS);

  Serial.println("\n==========================================");
  Serial.print("[DEV_WIFI] Connecting to " + String(DEV_SSID));

  // Connect loop with 25-second timeout and blinking blue LED
  unsigned long startAttempt = millis();
  bool ledState = false;
  while (WiFi.status() != WL_CONNECTED && millis() - startAttempt < 25000) {
    delay(250);
    ledState = !ledState;
    digitalWrite(LED_PIN, ledState ? HIGH : LOW);
    Serial.print(".");
  }

  if (WiFi.status() == WL_CONNECTED) {
    digitalWrite(LED_PIN, HIGH); // Solid Blue LED = Connected & Ready!
    Serial.println("\n[DEV_ONLINE] Connected successfully!");
    Serial.println("[ESP32_IP]    http://" + WiFi.localIP().toString());
    if (MDNS.begin("halloween")) {
      Serial.println("[MDNS_READY]  http://halloween.local");
    }
  } else {
    digitalWrite(LED_PIN, LOW);
    Serial.println("\n[DEV_WARNING] Could not connect to Wi-Fi. Check SSID/Password.");
  }
  Serial.println("==========================================\n");
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

  // 2. Start either Dev Station Mode or Production Access Point
  if (isDev) {
    startDevStation();
  } else {
    startPureAccessPoint();
  }

  // 3. Web Dashboard (Visit ESP32 IP in browser to test/monitor)
  server.on("/", HTTP_GET, []() {
    addCorsHeaders();
    String activeIP = getActiveIP().toString();
    String modeName = isDev ? "Home Wi-Fi (STA)" : "Standalone AP";
    String clientInfo = isDev ? (WiFi.status() == WL_CONNECTED ? "Connected (RSSI: " + String(WiFi.RSSI()) + " dBm)" : "Disconnected")
                              : (String(WiFi.softAPgetStationNum()) + " Kiosks");

    String html = "<!DOCTYPE html><html><head><meta name='viewport' content='width=device-width,initial-scale=1'>";
    html += "<style>body{background:#111;color:#eee;font-family:sans-serif;text-align:center;padding:30px;}";
    html += ".box{background:#1c1c22;padding:25px;border-radius:12px;display:inline-block;border:2px solid #ff5500;}";
    html += "button{background:#ff5500;color:#fff;border:none;padding:12px 24px;font-size:16px;border-radius:6px;cursor:pointer;margin-top:15px;font-weight:bold;}</style></head><body>";
    html += "<div class='box'><h1>ESP32 Door Node (" + modeName + ")</h1>";
    html += "<p><b>IP Address:</b> http://" + activeIP + " | <b>CPU:</b> " + String(getCpuFrequencyMhz()) + " MHz</p>";
    html += "<p><b>Uptime:</b> <span id='u'>0</span> mins | <b>Network:</b> " + clientInfo + "</p>";
    html += "<p style='font-size:24px;color:#00ff88;'><b>Distance:</b> <span id='d'>" + String(currentDistance) + "</span> cm</p>";
    html += "<p><b>Door State:</b> <span id='s'>" + String(doorClosed ? "CLOSED" : "OPEN") + "</span></p>";
    html += "<p><b>Total Triggers:</b> <span id='t'>" + String(triggerCount) + "</span></p>";
    html += "<button onclick=\"fetch('/test')\">Fire Test Trigger</button></div>";
    html += "<script>setInterval(()=>{fetch('/status').then(r=>r.json()).then(d=>{";
    html += "document.getElementById('d').innerText=d.distance;";
    html += "document.getElementById('s').innerText=d.doorOpen?'OPEN':'CLOSED';";
    html += "document.getElementById('t').innerText=d.triggers;";
    html += "document.getElementById('u').innerText=d.uptimeMin;});},400);</script></body></html>";
    server.send(200, "text/html", html);
  });

  // 4. Fast JSON Endpoint Polled by Kiosk (http://<ESP32_IP>/status)
  server.on("/status", HTTP_GET, []() {
    addCorsHeaders();
    String activeIP = getActiveIP().toString();
    int clientCount = isDev ? (WiFi.status() == WL_CONNECTED ? 1 : 0) : WiFi.softAPgetStationNum();
    String json = "{\"triggers\":" + String(triggerCount) +
                  ",\"distance\":" + String(currentDistance) +
                  ",\"doorOpen\":" + String(!doorClosed ? "true" : "false") +
                  ",\"uptimeMin\":" + String(millis() / 60000) +
                  ",\"ip\":\"" + activeIP + "\"" +
                  ",\"isDev\":" + String(isDev ? "true" : "false") +
                  ",\"clients\":" + String(clientCount) + "}";
    server.send(200, "application/json", json);
  });

  // 5. Manual Test Endpoint
  server.on("/test", HTTP_GET, []() {
    addCorsHeaders();
    triggerCount++;
    lastTriggerTime = millis();
    Serial.printf("[TEST_TRIGGER] #%lu\n", triggerCount);
    broadcastTrigger();
    server.send(200, "text/plain", "Triggered!");
  });

  server.begin();

  // 6. Enable 15-Second Hardware Watchdog (Auto-reboots if MCU ever freezes over 14 days)
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

  // 0. Non-blocking Wi-Fi reconnect in Dev mode if connection drops
  if (isDev && WiFi.status() != WL_CONNECTED) {
    static unsigned long lastReconnectAttempt = 0;
    if (now - lastReconnectAttempt >= 10000) {
      lastReconnectAttempt = now;
      Serial.println("[DEV_WIFI] Connection lost. Attempting reconnection...");
      WiFi.reconnect();
    }
  }

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
      broadcastTrigger();
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
    String activeIP = getActiveIP().toString();
    String beacon = "[ESP32_BEACON] IP: http://" + activeIP + " | Dist: " + String(currentDistance) +
                    "cm | Triggers: " + String(triggerCount) +
                    " | Mode: " + String(isDev ? "DEV_STA" : "PROD_AP");
    Serial.println(beacon);

    // Broadcast on UDP 4210
    IPAddress broadcastIP = isDev ? IPAddress(255, 255, 255, 255) : IPAddress(192, 168, 4, 255);
    udp.beginPacket(broadcastIP, 4210);
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

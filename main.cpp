#include <WiFi.h>
#include <WebServer.h>
#include <ESPmDNS.h>
#include <WiFiUdp.h>

const char* ssid = "YOUR_WIFI_SSID";
const char* password = "YOUR_WIFI_PASSWORD";

WebServer server(80);
WiFiUDP udp;

// 30-Pin ESP32 DevKit Pins
const int LED_PIN = 2;   // Onboard Blue LED
const int TRIG_PIN = 5;
const int ECHO_PIN = 18;

bool doorClosed = true;
unsigned long lastTriggerTime = 0;
unsigned long triggerCount = 0;
long currentDistance = 0;

const int DOOR_OPEN_THRESHOLD = 25;
const int DOOR_CLOSED_THRESHOLD = 12;
const unsigned long COOLDOWN_MS = 8000;

long getDistanceCm() {
  digitalWrite(TRIG_PIN, LOW);
  delayMicroseconds(2);
  digitalWrite(TRIG_PIN, HIGH);
  delayMicroseconds(10);
  digitalWrite(TRIG_PIN, LOW);
  long duration = pulseIn(ECHO_PIN, HIGH, 25000);
  if (duration == 0) return 999;
  return duration * 0.034 / 2;
}

void addCorsHeaders() {
  server.sendHeader("Access-Control-Allow-Origin", "*");
  server.sendHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
  server.sendHeader("Access-Control-Allow-Headers", "*");
}

void connectWiFi() {
  digitalWrite(LED_PIN, LOW);
  WiFi.mode(WIFI_STA);
  WiFi.setSleep(false);
  WiFi.begin(ssid, password);

  Serial.print("Connecting to Wi-Fi");
  while (WiFi.status() != WL_CONNECTED) {
    // Fast blink LED while connecting
    digitalWrite(LED_PIN, !digitalRead(LED_PIN));
    delay(250);
    Serial.print(".");
  }

  // Solid ON when connected!
  digitalWrite(LED_PIN, HIGH);
  Serial.println("\n[WIFI_CONNECTED]");
  Serial.print("[ESP32_IP] http://");
  Serial.println(WiFi.localIP());

  // Start mDNS so you can also just use http://halloween.local
  if (MDNS.begin("halloween")) {
    Serial.println("[MDNS_READY] http://halloween.local");
  }
}

void setup() {
  Serial.begin(115200);
  pinMode(LED_PIN, OUTPUT);
  pinMode(TRIG_PIN, OUTPUT);
  pinMode(ECHO_PIN, INPUT);

  connectWiFi();

  server.on("/", HTTP_GET, []() {
    addCorsHeaders();
    String html = "<!DOCTYPE html><html><head><meta name='viewport' content='width=device-width,initial-scale=1'>";
    html += "<style>body{background:#111;color:#eee;font-family:sans-serif;text-align:center;padding:30px;}";
    html += ".box{background:#222;padding:20px;border-radius:10px;display:inline-block;border:1px solid #444;}";
    html += "button{background:#ff5500;color:#fff;border:none;padding:12px 24px;font-size:16px;border-radius:6px;cursor:pointer;margin-top:15px;}</style></head><body>";
    html += "<div class='box'><h1>🎃 ESP32 Door Sensor Node</h1>";
    html += "<p><b>IP:</b> " + WiFi.localIP().toString() + " (or halloween.local)</p>";
    html += "<p><b>Distance:</b> <span id='d'>" + String(currentDistance) + "</span> cm</p>";
    html += "<p><b>Door State:</b> <span id='s'>" + String(doorClosed ? "CLOSED" : "OPEN") + "</span></p>";
    html += "<p><b>Total Triggers:</b> <span id='t'>" + String(triggerCount) + "</span></p>";
    html += "<button onclick=\"fetch('/test')\">🔥 Fire Test Trigger</button></div>";
    html += "<script>setInterval(()=>{fetch('/status').then(r=>r.json()).then(d=>{";
    html += "document.getElementById('d').innerText=d.distance;";
    html += "document.getElementById('s').innerText=d.doorOpen?'OPEN':'CLOSED';";
    html += "document.getElementById('t').innerText=d.triggers;});},500);</script></body></html>";
    server.send(200, "text/html", html);
  });

  server.on("/status", HTTP_GET, []() {
    addCorsHeaders();
    String json = "{\"triggers\":" + String(triggerCount) + 
                  ",\"distance\":" + String(currentDistance) + 
                  ",\"doorOpen\":" + String(!doorClosed ? "true" : "false") + "}";
    server.send(200, "application/json", json);
  });

  server.on("/test", HTTP_GET, []() {
    addCorsHeaders();
    triggerCount++;
    lastTriggerTime = millis();
    Serial.printf("[TEST_TRIGGER] #%lu\n", triggerCount);
    server.send(200, "text/plain", "Triggered!");
  });

  server.begin();
}

void loop() {
  // Auto-reconnect and turn off LED if Wi-Fi drops
  if (WiFi.status() != WL_CONNECTED) {
    connectWiFi();
  }

  server.handleClient();
  unsigned long now = millis();

  // Read Ultrasonic Sensor every 60ms
  static unsigned long lastSensorRead = 0;
  if (now - lastSensorRead >= 60) {
    lastSensorRead = now;
    currentDistance = getDistanceCm();

    if (currentDistance > DOOR_OPEN_THRESHOLD && doorClosed && (now - lastTriggerTime > COOLDOWN_MS)) {
      doorClosed = false;
      lastTriggerTime = now;
      triggerCount++;
      Serial.printf("[DOOR_OPEN] Trigger #%lu | Dist: %ld cm | IP: http://%s\n", 
                    triggerCount, currentDistance, WiFi.localIP().toString().c_str());
    }

    if (currentDistance < DOOR_CLOSED_THRESHOLD && !doorClosed && (now - lastTriggerTime > 3000)) {
      doorClosed = true;
      Serial.printf("[DOOR_CLOSED] Re-armed | Dist: %ld cm\n", currentDistance);
    }
  }

  // Broadcast IP over USB Serial AND Wi-Fi UDP every 3 seconds
  static unsigned long lastBeacon = 0;
  if (now - lastBeacon >= 3000) {
    lastBeacon = now;
    String beacon = "[ESP32_BEACON] IP: http://" + WiFi.localIP().toString() + 
                    " | Dist: " + String(currentDistance) + "cm | Triggers: " + String(triggerCount);
    Serial.println(beacon);

    // Also broadcast over UDP port 4210 so .NET CLI can find it without a USB cable!
    udp.beginPacket(IPAddress(255, 255, 255, 255), 4210);
    udp.print(beacon);
    udp.endPacket();
  }
}

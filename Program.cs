using System;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

class Program
{
    static readonly string LogFile = "esp_serial_log.txt";

    static async Task Main(string[] args)
    {
        Console.Title = "🎃 ESP32 Halloween Serial & Network Logger";
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("=====================================================");
        Console.WriteLine("   🎃 HALLOWEEN ESP32 IP FINDER & SERIAL LOGGER");
        Console.WriteLine("=====================================================\n");
        Console.ResetColor();

        // 1. Start Wireless UDP Listener in background (catches ESP32 IP even without USB!)
        _ = Task.Run(ListenForWirelessUdpBeacon);

        // 2. Scan for USB Serial COM Ports
        string[] ports = SerialPort.GetPortNames();
        if (ports.Length == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[!] No USB COM ports detected right now.");
            Console.WriteLine("[*] Listening wirelessly on UDP Port 4210 for ESP32 IP broadcasts...");
            Console.ResetColor();
            await Task.Delay(-1); // Keep running for UDP broadcasts
            return;
        }

        Console.WriteLine("Available COM Ports:");
        for (int i = 0; i < ports.Length; i++)
        {
            Console.WriteLine($"  [{i}] {ports[i]}");
        }

        string selectedPort = ports[0];
        if (ports.Length > 1)
        {
            Console.Write($"\nSelect port index (default 0 for {ports[0]}): ");
            string? input = Console.ReadLine();
            if (int.TryParse(input, out int idx) && idx >= 0 && idx < ports.Length)
            {
                selectedPort = ports[idx];
            }
        }

        // 3. Open Serial Port & Log
        try
        {
            using var serial = new SerialPort(selectedPort, 115200)
            {
                DtrEnable = false, // Prevents accidental ESP32 reset loop on some USB chips
                RtsEnable = false,
                ReadTimeout = 2000
            };

            serial.Open();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\n[+] Connected to {selectedPort} at 115200 baud. Logging to {LogFile}...\n");
            Console.ResetColor();

            while (true)
            {
                try
                {
                    string line = serial.ReadLine().Trim();
                    if (!string.IsNullOrEmpty(line))
                    {
                        PrintAndLog("USB", line);
                    }
                }
                catch (TimeoutException) { }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[!] Serial Error: {ex.Message}");
            Console.WriteLine("[*] Still listening wirelessly on UDP Port 4210...");
            Console.ResetColor();
            await Task.Delay(-1);
        }
    }

    static async Task ListenForWirelessUdpBeacon()
    {
        try
        {
            using var udp = new UdpClient(4210);
            while (true)
            {
                var result = await udp.ReceiveAsync();
                string msg = Encoding.UTF8.GetString(result.Buffer).Trim();
                PrintAndLog("UDP-WIFI", msg);
            }
        }
        catch { /* Port already in use or firewall blocked */ }
    }

    static void PrintAndLog(string source, string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string fullEntry = $"[{timestamp}] [{source}] {message}";

        // Highlight IP and Triggers in bright colors
        if (message.Contains("http://"))
        {
            Console.ForegroundColor = ConsoleColor.Green;
        }
        else if (message.Contains("[DOOR_OPEN]") || message.Contains("[TEST_TRIGGER]"))
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Gray;
        }

        Console.WriteLine(fullEntry);
        Console.ResetColor();

        // Append to log file
        try
        {
            File.AppendAllText(LogFile, fullEntry + Environment.NewLine);
        }
        catch { }
    }
}

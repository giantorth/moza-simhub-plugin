using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections;
using System.IO.Ports;
using System.Linq;
using System.Reflection;
using System.Threading;


namespace MozaPlugin.Protocol
{
    public class MozaSerialConnection : IDisposable
    {
        private volatile SerialPort? _port;
        private Thread? _readThread;
        private Thread? _writeThread;
        private readonly ConcurrentQueue<byte[]> _writeQueue = new ConcurrentQueue<byte[]>();
        private volatile bool _running;
        private readonly object _lock = new object();
        private string? _lastPortName;

        public event Action<byte[]>? MessageReceived;
        public bool IsConnected => _port?.IsOpen == true;

        /// <summary>
        /// The HID Product ID discovered from WMI during device enumeration.
        /// Null if PID could not be determined (e.g. probe-based discovery under Wine).
        /// </summary>
        public string? DiscoveredPid { get; private set; }

        public bool Connect()
        {
            // Try the last known port first to avoid re-probing (which
            // opens/closes the port and can reset the device under Wine).
            if (_lastPortName != null && TryOpen(_lastPortName))
                return true;

            var (portName, pid) = FindMozaPort();
            if (portName == null)
                return false;

            if (pid != null)
                DiscoveredPid = pid;

            return TryOpen(portName);
        }

        private bool TryOpen(string portName)
        {
            // Drain any stale messages from a previous connection
            while (_writeQueue.TryDequeue(out _)) { }

            try
            {
                _port = new SerialPort(portName, MozaProtocol.BaudRate)
                {
                    ReadTimeout = 500,
                    WriteTimeout = 500
                };
                _port.Open();
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();

                _running = true;
                _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "MozaSerialRead" };
                _writeThread = new Thread(WriteLoop) { IsBackground = true, Name = "MozaSerialWrite" };
                _readThread.Start();
                _writeThread.Start();

                _lastPortName = portName;
                SimHub.Logging.Current.Info($"[Moza] Connected to {portName}");
                return true;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[Moza] Failed to connect to {portName}: {ex.Message}");
                return false;
            }
        }

        public void Disconnect()
        {
            _running = false;
            _readThread?.Join(1000);
            _writeThread?.Join(1000);

            lock (_lock)
            {
                if (_port?.IsOpen == true)
                {
                    try { _port.Close(); }
                    catch (Exception ex) { SimHub.Logging.Current.Debug($"[Moza] Port close: {ex.Message}"); }
                }
                _port = null!;
            }
        }

        public void Send(byte[] message)
        {
            if (message != null)
                _writeQueue.Enqueue(message);
        }

        private void ReadLoop()
        {
            SimHub.Logging.Current.Info("[Moza] Read thread started");
            int messageCount = 0;

            while (_running)
            {
                try
                {
                    // Snapshot _port to avoid race with Disconnect() nullifying the field
                    var port = _port;
                    if (port == null || !port.IsOpen)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    // Poll for available data (works better under Wine than blocking ReadByte)
                    if (port.BytesToRead == 0)
                    {
                        Thread.Sleep(2);
                        continue;
                    }

                    // Read until we find the start byte
                    int b = port.ReadByte();
                    if (b != MozaProtocol.MessageStart)
                        continue;

                    // Wait for payload length byte
                    int waitMs = 0;
                    while (port.BytesToRead < 1 && waitMs < 200)
                    {
                        Thread.Sleep(1);
                        waitMs++;
                    }
                    if (port.BytesToRead < 1) continue;

                    int payloadLength = port.ReadByte();
                    if (payloadLength < 2 || payloadLength > 64)
                        continue;

                    // Wait for remaining bytes: group + device_id + payload + checksum
                    int needed = payloadLength + 3;
                    waitMs = 0;
                    while (port.BytesToRead < needed && waitMs < 200)
                    {
                        Thread.Sleep(1);
                        waitMs++;
                    }

                    int available = port.BytesToRead;
                    if (available < needed) continue;

                    var raw = new byte[needed];
                    int totalRead = 0;
                    while (totalRead < raw.Length)
                    {
                        int read = port.Read(raw, totalRead, raw.Length - totalRead);
                        if (read <= 0) break;
                        totalRead += read;
                    }

                    if (totalRead == raw.Length)
                    {
                        // Validate checksum: rebuild the full wire frame for calculation
                        // Wire frame = [start][payloadLength][group][dev][cmdPayload...]
                        var wireFrame = new byte[2 + payloadLength + 2]; // start + N + group + dev + cmdPayload
                        wireFrame[0] = MozaProtocol.MessageStart;
                        wireFrame[1] = (byte)payloadLength;
                        Array.Copy(raw, 0, wireFrame, 2, payloadLength + 2); // group + dev + cmdPayload
                        byte expected = MozaProtocol.CalculateChecksum(wireFrame);
                        byte actual = raw[raw.Length - 1];

                        if (expected != actual)
                        {
                            SimHub.Logging.Current.Debug(
                                $"[Moza] Checksum mismatch: expected=0x{expected:X2} actual=0x{actual:X2}, dropping message");
                            continue;
                        }

                        // Checksum escape: when checksum == 0x7E, sender doubles it on the wire.
                        // Consume the extra byte so it doesn't desync the next frame read.
                        if (actual == MozaProtocol.MessageStart && port.BytesToRead > 0)
                            port.ReadByte();

                        // Strip the checksum byte before passing to the parser
                        var data = new byte[needed - 1];
                        Array.Copy(raw, 0, data, 0, data.Length);

                        messageCount++;
                        if (messageCount <= 5)
                        {
                            SimHub.Logging.Current.Info(
                                $"[Moza] Received msg #{messageCount}: len={payloadLength} " +
                                $"group=0x{data[0]:X2} dev=0x{data[1]:X2} ({data.Length} bytes)");
                        }
                        MessageReceived?.Invoke(data);
                    }
                }
                catch (TimeoutException)
                {
                    // Normal timeout under Wine, continue
                }
                catch (Exception ex)
                {
                    if (_running)
                        SimHub.Logging.Current.Error($"[Moza] Read error: {ex.GetType().Name}: {ex.Message}");
                    Thread.Sleep(100);
                }
            }
        }

        private void WriteLoop()
        {
            SimHub.Logging.Current.Info("[Moza] Write thread started");
            int writeCount = 0;

            while (_running)
            {
                if (_writeQueue.TryDequeue(out var msg))
                {
                    try
                    {
                        lock (_lock)
                        {
                            _port?.Write(msg, 0, msg.Length);
                            // Checksum escape: when the last byte (checksum) is 0x7E,
                            // double it on the wire so the receiver doesn't treat it
                            // as the start of a new frame.
                            if (msg[msg.Length - 1] == MozaProtocol.MessageStart)
                                _port?.Write(new byte[] { MozaProtocol.MessageStart }, 0, 1);
                        }
                        writeCount++;
                        if (writeCount <= 5)
                            SimHub.Logging.Current.Info($"[Moza] Sent cmd #{writeCount}: {msg.Length} bytes, group=0x{(msg.Length > 2 ? msg[2] : 0):X2}");

                        // Pace writes: Moza bases drop commands when flooded with rapid-fire
                        // settings writes (e.g. ApplyProfile sends 30+ commands in a burst).
                        // 4ms matches boxflat's proven timing for reliable device writes.
                        if (!_writeQueue.IsEmpty)
                            Thread.Sleep(4);
                    }
                    catch (Exception ex)
                    {
                        if (_running)
                            SimHub.Logging.Current.Error($"[Moza] Write error: {ex.Message}");
                    }
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
        }

        private static (string? PortName, string? Pid) FindMozaPort()
        {
            // Try WMI-based discovery first (native Windows), loaded optionally via reflection
            try
            {
                var asm = Assembly.Load("System.Management");
                var searcherType = asm.GetType("System.Management.ManagementObjectSearcher")!;
                using var searcher = (IDisposable)Activator.CreateInstance(
                    searcherType, "SELECT * FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'")!;
                var collection = (IDisposable)searcherType.GetMethod("Get", Type.EmptyTypes)!.Invoke(searcher, null)!;
                using (collection)
                {
                    foreach (var obj in (IEnumerable)collection)
                    {
                        var objType = obj.GetType();
                        var prop = objType.GetProperty("Item", new[] { typeof(string) })!;
                        var deviceId = prop.GetValue(obj, new object[] { "DeviceID" })?.ToString() ?? "";
                        var caption = prop.GetValue(obj, new object[] { "Caption" })?.ToString() ?? "";

                        if (deviceId.IndexOf("VID_346E", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            int comStart = caption.IndexOf("(COM");
                            if (comStart >= 0)
                            {
                                int comEnd = caption.IndexOf(")", comStart);
                                if (comEnd > comStart)
                                {
                                    var portName = caption.Substring(comStart + 1, comEnd - comStart - 1);
                                    var pid = ExtractPid(deviceId);
                                    SimHub.Logging.Current.Info($"[Moza] Found Moza device on {portName} PID={pid ?? "unknown"} (WMI)");
                                    return (portName, pid);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info($"[Moza] WMI discovery unavailable ({ex.GetType().Name}), trying probe");
            }

            // Probe-based discovery: try opening each COM port and sending a Moza read command.
            // This works under Proton/Wine where COM ports are symlinked to /dev/ttyACM*.
            // We probe high-numbered ports first since Wine maps ttyACM devices to COM33+.
            var ports = SerialPort.GetPortNames();
            Array.Sort(ports, (a, b) =>
            {
                int na = ExtractPortNumber(a);
                int nb = ExtractPortNumber(b);
                return nb.CompareTo(na); // Descending - check high ports first
            });

            foreach (var port in ports)
            {
                if (ProbeMozaDevice(port))
                {
                    SimHub.Logging.Current.Info($"[Moza] Found Moza device on {port} (probe)");
                    return (port, null);
                }
            }

            SimHub.Logging.Current.Info("[Moza] No MOZA device found on any COM port");
            return (null, null);
        }

        /// <summary>
        /// Extract the PID from a WMI DeviceID string.
        /// Format: USB\VID_346E&amp;PID_XXXX\...
        /// Returns the PID as a hex string like "0x0006", or null if not found.
        /// </summary>
        private static string? ExtractPid(string deviceId)
        {
            int pidIndex = deviceId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
            if (pidIndex < 0 || pidIndex + 8 > deviceId.Length)
                return null;

            var pidHex = deviceId.Substring(pidIndex + 4, 4);

            // Validate it's actually hex
            foreach (char c in pidHex)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f')))
                    return null;
            }

            return "0x" + pidHex.ToUpperInvariant();
        }

        /// <summary>
        /// Try to open a port and send Moza read commands to the base and hub.
        /// If we get a valid response starting with 0x7E, it's a Moza device.
        /// Sends both probes so hub-only setups (no wheelbase) are also discovered.
        /// </summary>
        private static bool ProbeMozaDevice(string portName)
        {
            try
            {
                using (var probe = new SerialPort(portName, MozaProtocol.BaudRate)
                {
                    ReadTimeout = 300,
                    WriteTimeout = 300
                })
                {
                    probe.Open();
                    probe.DiscardInBuffer();

                    // Send a read request for base-state (read group 43, device base 19, cmd id 1)
                    var baseMsg = new byte[] { 0x7E, 0x03, 0x2B, 0x13, 0x01, 0x00, 0x01, 0x00 };
                    baseMsg[baseMsg.Length - 1] = MozaProtocol.CalculateChecksum(baseMsg, baseMsg.Length - 1);
                    probe.Write(baseMsg, 0, baseMsg.Length);

                    // Also probe the hub (read group 100, device hub 18, cmd id 3 = port1)
                    // Hub-only setups have no base to respond to the first probe.
                    var hubMsg = new byte[] { 0x7E, 0x03, 0x64, 0x12, 0x03, 0x00, 0x00, 0x00 };
                    hubMsg[hubMsg.Length - 1] = MozaProtocol.CalculateChecksum(hubMsg, hubMsg.Length - 1);
                    probe.Write(hubMsg, 0, hubMsg.Length);

                    // Wait briefly for a response from either device
                    System.Threading.Thread.Sleep(100);

                    if (probe.BytesToRead > 0)
                    {
                        int first = probe.ReadByte();
                        probe.Close();
                        return first == MozaProtocol.MessageStart;
                    }
                    probe.Close();
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Debug($"[Moza] Probe {portName}: {ex.GetType().Name}");
            }
            return false;
        }

        private static int ExtractPortNumber(string portName)
        {
            int num = 0;
            for (int i = 0; i < portName.Length; i++)
            {
                if (portName[i] >= '0' && portName[i] <= '9')
                    num = num * 10 + (portName[i] - '0');
            }
            return num;
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}

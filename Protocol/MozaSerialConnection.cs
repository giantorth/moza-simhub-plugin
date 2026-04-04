using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections;
using System.IO.Ports;
using System.Linq;
using System.Reflection;
using System.Threading;


namespace MozaTelemetryPlugin.Protocol
{
    public class MozaSerialConnection : IDisposable
    {
        private SerialPort? _port;
        private Thread? _readThread;
        private Thread? _writeThread;
        private readonly ConcurrentQueue<byte[]> _writeQueue = new ConcurrentQueue<byte[]>();
        private volatile bool _running;
        private readonly object _lock = new object();

        public event Action<byte[]>? MessageReceived;
        public bool IsConnected => _port?.IsOpen == true;

        public bool Connect()
        {
            var portName = FindMozaPort();
            if (portName == null)
                return false;

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

                SimHub.Logging.Current.Info($"[MozaTelemetry] Connected to {portName}");
                return true;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[MozaTelemetry] Failed to connect to {portName}: {ex.Message}");
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
                    try { _port.Close(); } catch { }
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
            SimHub.Logging.Current.Info("[MozaTelemetry] Read thread started");
            int messageCount = 0;

            while (_running)
            {
                try
                {
                    if (_port == null || !_port.IsOpen)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    // Poll for available data (works better under Wine than blocking ReadByte)
                    if (_port.BytesToRead == 0)
                    {
                        Thread.Sleep(2);
                        continue;
                    }

                    // Read until we find the start byte
                    int b = _port.ReadByte();
                    if (b != MozaProtocol.MessageStart)
                        continue;

                    // Wait for payload length byte
                    int waitMs = 0;
                    while (_port.BytesToRead < 1 && waitMs < 200)
                    {
                        Thread.Sleep(1);
                        waitMs++;
                    }
                    if (_port.BytesToRead < 1) continue;

                    int payloadLength = _port.ReadByte();
                    if (payloadLength < 2 || payloadLength > 64)
                        continue;

                    // Wait for remaining bytes: group + device_id + payload = payloadLength + 2
                    int needed = payloadLength + 2;
                    waitMs = 0;
                    while (_port.BytesToRead < needed && waitMs < 200)
                    {
                        Thread.Sleep(1);
                        waitMs++;
                    }

                    int available = _port.BytesToRead;
                    if (available < needed) continue;

                    var data = new byte[needed];
                    int totalRead = 0;
                    while (totalRead < data.Length)
                    {
                        int read = _port.Read(data, totalRead, data.Length - totalRead);
                        if (read <= 0) break;
                        totalRead += read;
                    }

                    if (totalRead == data.Length)
                    {
                        messageCount++;
                        if (messageCount <= 5)
                        {
                            SimHub.Logging.Current.Info(
                                $"[MozaTelemetry] Received msg #{messageCount}: len={payloadLength} " +
                                $"group=0x{data[0]:X2} dev=0x{data[1]:X2} ({totalRead} bytes)");
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
                        SimHub.Logging.Current.Error($"[MozaTelemetry] Read error: {ex.GetType().Name}: {ex.Message}");
                    Thread.Sleep(100);
                }
            }
        }

        private void WriteLoop()
        {
            SimHub.Logging.Current.Info("[MozaTelemetry] Write thread started");
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
                        }
                        writeCount++;
                        if (writeCount <= 5)
                            SimHub.Logging.Current.Info($"[MozaTelemetry] Sent cmd #{writeCount}: {msg.Length} bytes, group=0x{(msg.Length > 2 ? msg[2] : 0):X2}");
                    }
                    catch (Exception ex)
                    {
                        if (_running)
                            SimHub.Logging.Current.Error($"[MozaTelemetry] Write error: {ex.Message}");
                    }
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
        }

        private static string? FindMozaPort()
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
                    var indexer = collection.GetType().GetProperty("Item", new[] { typeof(int) });
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
                                    SimHub.Logging.Current.Info($"[MozaTelemetry] Found Moza device on {portName} (WMI)");
                                    return portName;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info($"[MozaTelemetry] WMI discovery unavailable ({ex.GetType().Name}), trying probe");
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
                    SimHub.Logging.Current.Info($"[MozaTelemetry] Found Moza device on {port} (probe)");
                    return port;
                }
            }

            SimHub.Logging.Current.Info("[MozaTelemetry] No MOZA device found on any COM port");
            return null;
        }

        /// <summary>
        /// Try to open a port and send a Moza base-state read command.
        /// If we get a valid response starting with 0x7E, it's a Moza device.
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
                    // Message: [0x7E] [len=3] [group=43] [device=19] [cmd=1] [payload=0x00,0x01] [checksum]
                    var msg = new byte[] { 0x7E, 0x03, 0x2B, 0x13, 0x01, 0x00, 0x01, 0x00 };
                    msg[msg.Length - 1] = MozaProtocol.CalculateChecksum(msg);
                    probe.Write(msg, 0, msg.Length);

                    // Wait briefly for a response
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
            catch
            {
                // Port busy, doesn't exist, or not a serial device - skip
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

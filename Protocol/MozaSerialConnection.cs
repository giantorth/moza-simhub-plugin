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
        private volatile bool _shutdownRequested;

        // Consecutive I/O error tracking. After sleep/resume the SerialPort handle
        // stays .IsOpen==true but every read/write throws IOException("Not ready"),
        // so nothing triggers reconnect. Count failures and force-close at threshold.
        private int _consecutiveIoErrors;
        private volatile bool _portFailureLogged;
        private const int PortDeadThreshold = 10;

        public event Action<byte[]>? MessageReceived;
        public bool IsConnected => _port?.IsOpen == true;

        /// <summary>
        /// The HID Product ID discovered from WMI during device enumeration.
        /// Null if PID could not be determined (e.g. probe-based discovery under Wine).
        /// </summary>
        public string? DiscoveredPid { get; private set; }

        public bool Connect()
        {
            if (_shutdownRequested)
                return false;

            // Tear down any stale threads/port from a previous dead session
            // (e.g. after sleep/resume killed the tty but handle stayed open).
            if (_running || _port != null)
                Disconnect();

            // Try the last known port first to avoid re-probing (which
            // opens/closes the port and can reset the device under Wine).
            if (_lastPortName != null && TryOpen(_lastPortName))
                return true;

            var (portName, pid) = FindMozaPort(() => _shutdownRequested);
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

                try
                {
                    _readThread.Start();
                    _writeThread.Start();
                }
                catch
                {
                    // If either start failed, tear down: signal stop, join whichever started,
                    // close port, then rethrow so the outer catch logs it.
                    _running = false;
                    try { _readThread?.Join(500); } catch { }
                    try { _writeThread?.Join(500); } catch { }
                    try { _port?.Close(); } catch { }
                    _port = null;
                    throw;
                }

                _lastPortName = portName;
                Interlocked.Exchange(ref _consecutiveIoErrors, 0);
                _portFailureLogged = false;
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
                _port = null;
            }
        }

        public void Send(byte[] message)
        {
            if (message != null)
                _writeQueue.Enqueue(message);
        }

        // Record an I/O failure. Throttles log spam and force-closes the port
        // once the failure count crosses the threshold so the reconnect timer
        // can reopen it (handles sleep/resume where .IsOpen stays true on dead tty).
        private void HandleIoFailure(string label, Exception ex)
        {
            if (!_running) return;

            int count = Interlocked.Increment(ref _consecutiveIoErrors);

            if (!_portFailureLogged)
            {
                SimHub.Logging.Current.Error($"[Moza] {label} error: {ex.GetType().Name}: {ex.Message}");
            }

            if (count >= PortDeadThreshold && !_portFailureLogged)
            {
                _portFailureLogged = true;
                SimHub.Logging.Current.Warn(
                    $"[Moza] Port wedged after {count} consecutive I/O errors — closing for reconnect");
                lock (_lock)
                {
                    try { _port?.Close(); } catch { }
                    _port = null;
                }
                while (_writeQueue.TryDequeue(out _)) { }
            }
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

                    // Read until we find the start byte. Raw 0x7E may also appear
                    // stuffed (as a payload byte from a prior frame we de-synced on);
                    // accept any 0x7E as a candidate frame start.
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

                    // Read N+3 decoded bytes (group + device + payload + checksum),
                    // collapsing 0x7E 0x7E wire pairs back to a single 0x7E byte.
                    // Moza byte-stuffing: every 0x7E after the start+len header is
                    // doubled on the wire. Sim and real firmware both emit this;
                    // without decoding, any 0x7E inside a payload (common in zlib
                    // blobs, telemetry data, seq counters) corrupts the read stream.
                    int needed = payloadLength + 3;
                    var raw = new byte[needed];
                    int decoded = 0;
                    bool frameError = false;
                    while (decoded < needed)
                    {
                        waitMs = 0;
                        while (port.BytesToRead < 1 && waitMs < 200)
                        {
                            Thread.Sleep(1);
                            waitMs++;
                        }
                        if (port.BytesToRead < 1) { frameError = true; break; }

                        int wb = port.ReadByte();
                        if (wb == MozaProtocol.MessageStart)
                        {
                            // Stuffed byte: expect a second 0x7E. Anything else means
                            // the sender began a new frame mid-transfer — abandon
                            // this one so the outer loop can re-sync on the 0x7E.
                            waitMs = 0;
                            while (port.BytesToRead < 1 && waitMs < 200)
                            {
                                Thread.Sleep(1);
                                waitMs++;
                            }
                            if (port.BytesToRead < 1) { frameError = true; break; }
                            int esc = port.ReadByte();
                            if (esc != MozaProtocol.MessageStart)
                            {
                                frameError = true;
                                break;
                            }
                            raw[decoded++] = (byte)MozaProtocol.MessageStart;
                        }
                        else
                        {
                            raw[decoded++] = (byte)wb;
                        }
                    }

                    if (frameError || decoded != needed)
                        continue;

                    // Validate checksum: rebuild the full decoded frame for calculation
                    var wireFrame = new byte[2 + payloadLength + 2];
                    wireFrame[0] = MozaProtocol.MessageStart;
                    wireFrame[1] = (byte)payloadLength;
                    Array.Copy(raw, 0, wireFrame, 2, payloadLength + 2);
                    byte expected = MozaProtocol.CalculateChecksum(wireFrame);
                    byte actual = raw[raw.Length - 1];

                    if (expected != actual)
                    {
                        SimHub.Logging.Current.Debug(
                            $"[Moza] Checksum mismatch: expected=0x{expected:X2} actual=0x{actual:X2}, dropping message");
                        continue;
                    }

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
                    Interlocked.Exchange(ref _consecutiveIoErrors, 0);
                    MessageReceived?.Invoke(data);
                }
                catch (TimeoutException)
                {
                    // Normal timeout under Wine, continue
                }
                catch (Exception ex)
                {
                    HandleIoFailure("Read", ex);
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
                            // Moza byte-stuffing: header (start + len) goes raw; every
                            // 0x7E byte after that is doubled on the wire so the
                            // receiver doesn't treat it as a new frame start. Applies
                            // to group, device, payload, and checksum alike.
                            if (msg.Length >= 2)
                                _port?.Write(msg, 0, 2);
                            byte stuff = MozaProtocol.MessageStart;
                            for (int i = 2; i < msg.Length; i++)
                            {
                                _port?.Write(msg, i, 1);
                                if (msg[i] == MozaProtocol.MessageStart)
                                    _port?.Write(new byte[] { stuff }, 0, 1);
                            }
                        }
                        writeCount++;
                        if (writeCount <= 5)
                            SimHub.Logging.Current.Info($"[Moza] Sent cmd #{writeCount}: {msg.Length} bytes, group=0x{(msg.Length > 2 ? msg[2] : 0):X2}");

                        Interlocked.Exchange(ref _consecutiveIoErrors, 0);

                        // Pace writes: Moza bases drop commands when flooded with rapid-fire
                        // settings writes (e.g. ApplyProfile sends 30+ commands in a burst).
                        // 4ms matches boxflat's proven timing for reliable device writes.
                        if (!_writeQueue.IsEmpty)
                            Thread.Sleep(4);
                    }
                    catch (Exception ex)
                    {
                        HandleIoFailure("Write", ex);
                    }
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
        }

        private static (string? PortName, string? Pid) FindMozaPort(Func<bool>? cancel = null)
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

            // Two-pass probe: bases first, then hubs. v0.7.0 sent both probes per port
            // and returned the first port with any 0x7E reply, which mis-selected the
            // hub when both base + hub were present, or when probe-cycle timing left
            // the base unresponsive after the wrong message hit it.
            //
            // 600ms budget per port — SerialPort.Open can hang indefinitely under Wine
            // if another process holds the tty. Background-thread the probe so one bad
            // port can't block all detection.
            var unreachable = new HashSet<string>();
            foreach (var port in ports)
            {
                if (cancel?.Invoke() == true) return (null, null);

                var (responded, reachable) = ProbeWithTimeout(port, 600, baseProbe: true);
                if (responded)
                {
                    SimHub.Logging.Current.Info($"[Moza] Found Moza base on {port} (probe)");
                    return (port, null);
                }
                if (!reachable) unreachable.Add(port);
            }

            foreach (var port in ports)
            {
                if (cancel?.Invoke() == true) return (null, null);
                if (unreachable.Contains(port)) continue;

                var (responded, _) = ProbeWithTimeout(port, 600, baseProbe: false);
                if (responded)
                {
                    SimHub.Logging.Current.Info($"[Moza] Found Moza hub on {port} (probe)");
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

        // Pre-built probe frames. Base targets group 43, device 19, cmd id 2 (state-change probe).
        // Hub targets group 100, device 18, cmd id 3 (port1 power).
        // Base probe arg matches PitHouse wire pattern (documented § Group 0x2B) so device
        // responses stay consistent across clients.
        private static readonly byte[] BaseProbeFrame = BuildProbe(new byte[] { 0x7E, 0x03, 0x2B, 0x13, 0x02, 0x00, 0x00, 0x00 });
        private static readonly byte[] HubProbeFrame  = BuildProbe(new byte[] { 0x7E, 0x03, 0x64, 0x12, 0x03, 0x00, 0x00, 0x00 });

        private static byte[] BuildProbe(byte[] frame)
        {
            frame[frame.Length - 1] = MozaProtocol.CalculateChecksum(frame, frame.Length - 1);
            return frame;
        }

        /// <summary>
        /// Run ProbeMozaDevice on a background thread with a hard time budget (ms).
        /// Returns (responded, reachable). reachable=false means open/timeout failed
        /// — caller can skip this port in subsequent passes.
        /// </summary>
        private static (bool responded, bool reachable) ProbeWithTimeout(string portName, int timeoutMs, bool baseProbe)
        {
            bool responded = false;
            bool reachable = false;
            var t = new Thread(() =>
            {
                try { (responded, reachable) = ProbeMozaDevice(portName, baseProbe); }
                catch { responded = false; reachable = false; }
            })
            { IsBackground = true, Name = $"MozaProbe-{portName}" };
            t.Start();
            if (!t.Join(timeoutMs))
            {
                SimHub.Logging.Current.Debug($"[Moza] Probe {portName}: timed out after {timeoutMs}ms, skipping");
                return (false, false);
            }
            return (responded, reachable);
        }

        /// <summary>
        /// Open port, send a single base-or-hub probe, wait for any 0x7E reply.
        /// Single-message probe avoids the v0.7.0 issue where back-to-back base+hub
        /// writes left the device in a state where it stopped answering after reopen.
        /// </summary>
        private static (bool responded, bool reachable) ProbeMozaDevice(string portName, bool baseProbe)
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

                    var msg = baseProbe ? BaseProbeFrame : HubProbeFrame;
                    probe.Write(msg, 0, msg.Length);

                    System.Threading.Thread.Sleep(100);

                    bool responded = false;
                    if (probe.BytesToRead > 0)
                    {
                        int first = probe.ReadByte();
                        responded = first == MozaProtocol.MessageStart;
                    }
                    probe.Close();
                    return (responded, true);
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Debug($"[Moza] Probe {portName}: {ex.GetType().Name}");
                return (false, false);
            }
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
            _shutdownRequested = true;
            Disconnect();
        }
    }
}

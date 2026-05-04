using System;
using System.Collections.Generic;
using System.Threading;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Mechanical layout the AB9 advertises to the host. Numeric value matches the
    /// single-byte payload of the <c>0x1F / 0xD3 00</c> mode-set command captured
    /// from PitHouse (see docs/protocol/devices/ab9-shifter.md).
    /// </summary>
    public enum Ab9Mode : byte
    {
        FivePlusR_L1 = 0x00,
        SixPlusR_L1  = 0x04,
        SixPlusR_L2  = 0x05,
        SevenPlusR_L1 = 0x06,
        SevenPlusR_L2 = 0x07,
        Sequential   = 0x09,
    }

    /// <summary>
    /// Configurable feel sliders exposed on the AB9. Each value maps to a 0..100
    /// integer payload sent verbatim to the device.
    /// </summary>
    public enum Ab9Slider
    {
        MechanicalResistance,
        Spring,
        NaturalDamping,
        NaturalFriction,
        MaxTorqueLimit,
    }

    /// <summary>
    /// Wraps a dedicated <see cref="MozaSerialConnection"/> for the AB9 active shifter
    /// (VID 0x346E, PID 0x1000). Owns the connection lifecycle, identity probe,
    /// mode/slider writes, and stored-state read-back. Independent of the wheelbase
    /// connection so both can run side-by-side.
    /// </summary>
    public class MozaAb9DeviceManager : IDisposable
    {
        // Slider command names registered in MozaCommandDatabase. Keep this in
        // sync with the AB9 entries added there — the lookup is the single source
        // of truth for cmdId bytes, so no opcode tables live here.
        private static readonly Dictionary<Ab9Slider, string> SliderCommands =
            new Dictionary<Ab9Slider, string>
            {
                { Ab9Slider.MechanicalResistance, "ab9-mech-resistance" },
                { Ab9Slider.Spring,               "ab9-spring" },
                { Ab9Slider.NaturalDamping,       "ab9-natural-damping" },
                { Ab9Slider.NaturalFriction,      "ab9-natural-friction" },
                { Ab9Slider.MaxTorqueLimit,       "ab9-max-torque-limit" },
            };

        public static IReadOnlyList<Ab9Slider> AllSliders { get; } = new[]
        {
            Ab9Slider.MechanicalResistance,
            Ab9Slider.Spring,
            Ab9Slider.NaturalDamping,
            Ab9Slider.NaturalFriction,
            Ab9Slider.MaxTorqueLimit,
        };

        private readonly MozaSerialConnection _connection;
        private volatile bool _detected;

        public bool Detected => _detected;
        public bool IsConnected => _connection.IsConnected;
        public MozaSerialConnection Connection => _connection;

        public event Action<byte[]>? MessageReceived
        {
            add    => _connection.MessageReceived += value;
            remove => _connection.MessageReceived -= value;
        }

        public MozaAb9DeviceManager()
        {
            // PID filter rejects everything that isn't the AB9 during WMI
            // enumeration — the wheelbase's own port discovery must not be
            // hijacked by this connection. When WMI is unavailable (Wine/Proton,
            // or a Windows install where System.Management can't be loaded into
            // SimHub's AppDomain) the probe path now sends an AB9-specific
            // identity probe (group 0x09 dev 0x12) and only accepts a response
            // whose group is 0x89, so it cannot match a wheelbase or hub.
            _connection = new MozaSerialConnection(MozaUsbIds.IsAb9Pid, MozaProbeTarget.Ab9);
            _connection.CaptureLabel = "ab9";
        }

        /// <summary>
        /// Attempt to open the AB9's COM port. Returns true if a connection
        /// was established (or was already up). Idempotent across reconnect
        /// attempts; the underlying serial connection re-uses the last known
        /// port name when possible to avoid re-probing.
        /// </summary>
        public bool TryConnect()
        {
            if (_connection.IsConnected) return true;
            bool ok = _connection.Connect();
            if (ok)
                MozaLog.Info("[Moza/AB9] Connected to AB9 shifter");
            return ok;
        }

        public void Disconnect()
        {
            _connection.Disconnect();
            _detected = false;
        }

        /// <summary>
        /// Mark the AB9 as detected (i.e. a recognisable response landed on this
        /// pipe). Latched true; reset only via <see cref="Disconnect"/> or
        /// <see cref="Dispose"/>. Logging happens on the rising edge so reconnects
        /// don't spam the log.
        /// </summary>
        public void MarkDetected()
        {
            if (_detected) return;
            _detected = true;
            MozaLog.Debug("[Moza/AB9] AB9 active shifter detected");
        }

        /// <summary>
        /// Send the PitHouse-style identity probe sequence (0x09 / 0x02 / 0x06 /
        /// 0x08 / 0x11) targeted at the AB9's main device id 0x12. Mirrors the
        /// wheelbase identity handshake — captured frames show PitHouse running
        /// the same probes against the AB9 on connect.
        /// </summary>
        public void SendIdentityProbe()
        {
            if (!_connection.IsConnected) return;
            SendRawProbe(0x09, null);
            SendRawProbe(0x02, null);
            SendRawProbe(0x06, null);
            SendRawProbe(0x08, new byte[] { 0x02 });
            SendRawProbe(0x11, new byte[] { 0x04 });
        }

        private void SendRawProbe(byte group, byte[]? payload)
        {
            int payloadLen = payload?.Length ?? 0;
            var frame = new byte[4 + payloadLen + 1];
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = (byte)payloadLen;
            frame[2] = group;
            frame[3] = MozaProtocol.DeviceAb9;
            if (payload != null)
                System.Buffer.BlockCopy(payload, 0, frame, 4, payloadLen);
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame);
            _connection.Send(frame);
        }

        /// <summary>
        /// Push the mechanical layout (5+R / 6+R / 7+R / Sequential) to the AB9.
        /// Single 8-byte CDC frame on group 0x1F with cmdId D3 00 + mode byte.
        /// </summary>
        public bool SendMode(Ab9Mode mode)
        {
            return WriteSliderRaw("ab9-mode", (byte)mode);
        }

        /// <summary>
        /// Push a slider value (0..100, clamped) to the AB9. Returns false if
        /// the connection is dead or the slider is not in the command database.
        /// </summary>
        public bool SendSlider(Ab9Slider slider, int value0to100)
        {
            if (!SliderCommands.TryGetValue(slider, out var commandName))
                return false;
            byte clamped = (byte)Math.Max(0, Math.Min(100, value0to100));
            return WriteSliderRaw(commandName, clamped);
        }

        /// <summary>
        /// Issue a read for every stored slider so the panel can populate from
        /// device state. Each read goes out as a separate one-shot frame and
        /// shares the connection's 4 ms pacing with the identity probe.
        /// </summary>
        public void RequestAllStoredSettings()
        {
            if (!_connection.IsConnected) return;
            ReadCommand("ab9-mode");
            foreach (var slider in AllSliders)
            {
                if (SliderCommands.TryGetValue(slider, out var name))
                    ReadCommand(name);
            }
        }

        private bool WriteSliderRaw(string commandName, byte value)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildWriteMessage(MozaProtocol.DeviceAb9, new byte[] { value });
            if (msg == null) return false;
            _connection.Send(msg);
            return true;
        }

        private void ReadCommand(string commandName)
        {
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return;
            var msg = cmd.BuildReadMessage(MozaProtocol.DeviceAb9);
            if (msg != null)
                _connection.Send(msg);
        }

        public void Dispose()
        {
            _connection.Dispose();
        }
    }
}

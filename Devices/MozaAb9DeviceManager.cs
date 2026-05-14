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

        public MozaAb9DeviceManager(Func<bool>? disableProbeFallback = null)
        {
            // PID filter accepts only the AB9 PID during registry-based
            // discovery — the wheelbase's own port enumeration is filtered
            // by its mirror predicate so neither connection can grab the
            // other's port. When the registry returns zero MOZA devices
            // (Wine/Proton, missing driver) the legacy serial-probe path
            // runs as a last resort; the AB9-specific identity probe
            // (group 0x09 dev 0x12) only accepts a 0x89 response and runs
            // a base-disambiguation pre-check first, so it cannot mis-claim
            // a wheelbase tty.
            _connection = new MozaSerialConnection(
                MozaUsbIds.IsAb9Pid,
                MozaProbeTarget.Ab9,
                disableProbeFallback);
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
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
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

        // ===== Host-rendered engine vibration (Group 0x20 / cmd 0x0A 0x05) =====
        //
        // PitHouse streams this at ~91 Hz with a 24-bit BE period field that
        // satisfies period = K / (engine_rpm × freq_hz), K ≈ 3.95e11. The
        // slot ID toggles between an active value and 0x0000 (silent
        // keepalive) when intensity drops to zero. See
        // docs/protocol/devices/ab9-shifter.md for the full decode.
        //
        // Layout of the 24-byte wire frame:
        //   7E 13 20 12 0A 05 [slot_hi slot_lo] [00 × 7]
        //                     [per_hi per_mid per_lo] 04 [00 × 4] [cksum]
        // Length byte 0x13 = 19 = cmd-id(2) + slot(2) + 7-zero + period(3)
        //                       + tag(1) + 4-zero.
        public const ushort SilentSlotId = 0x0000;
        public const ushort DefaultEngineVibSlotId = 0x1996;
        public const uint MinPeriodTicks = 0x64;
        public const uint MaxPeriodTicks = 0xFFFFFF;

        /// <summary>
        /// Push one frame of the engine-vibration stream. When <paramref name="active"/>
        /// is false the silent-keepalive slot (0x0000) is used and the period
        /// becomes a stable mid-range filler. The frame goes through the
        /// latest-wins stream lane so worker stalls never pile stale frames
        /// on the wire.
        /// </summary>
        public bool SendEngineVibrationStream(bool active, uint periodTicks)
        {
            if (!_connection.IsConnected) return false;
            ushort slot = active ? DefaultEngineVibSlotId : SilentSlotId;
            if (periodTicks < MinPeriodTicks) periodTicks = MinPeriodTicks;
            if (periodTicks > MaxPeriodTicks) periodTicks = MaxPeriodTicks;

            var frame = new byte[24];
            frame[0] = MozaProtocol.MessageStart; // 0x7E
            frame[1] = 0x13;                      // length = cmd(2) + payload(17)
            frame[2] = 0x20;                      // group: FFB
            frame[3] = MozaProtocol.DeviceAb9;    // dev id 0x12
            frame[4] = 0x0A;                      // cmd hi
            frame[5] = 0x05;                      // cmd lo: streaming refresh
            frame[6] = (byte)(slot >> 8);
            frame[7] = (byte)(slot & 0xFF);
            // frame[8..14] left zero (already zero-initialised)
            frame[15] = (byte)((periodTicks >> 16) & 0xFF);
            frame[16] = (byte)((periodTicks >> 8) & 0xFF);
            frame[17] = (byte)(periodTicks & 0xFF);
            frame[18] = 0x04;                     // type tag
            // frame[19..22] trailing zeros
            frame[23] = MozaProtocol.CalculateWireChecksum(frame, 23);
            _connection.SendStream(StreamKind.Ab9EngineVibration, frame);
            return true;
        }

        // ===== Gear-shift vibration intensity config (Group 0x20 / cmd 0x0A 0x01) =====
        //
        // One-shot push on slider change. AB9 firmware persists the value and
        // fires the rumble pattern itself on every HID-detected gear shift —
        // no per-shift host trigger needed (see ab9-shifter.md).
        //
        // Layout of the 24-byte wire frame:
        //   7E 13 20 12 0A 01 [int_hi int_lo] [00 × 7]
        //                     [0E 00 64 04] [00 × 4] [cksum]
        // Intensity is BE 16-bit, linearly scaled from 0..100 to 0..0x332C
        // (verified: 30% = 0x0F5A, 100% = 0x332C).
        private const ushort MaxGearShiftIntensityRaw = 0x332C;

        /// <summary>
        /// Push the stored gear-shift-vibration intensity (0..100) to the AB9.
        /// Goes through the one-shot FIFO so it preserves order against the
        /// other slider writes that follow in <c>ApplySavedAb9Settings</c>.
        /// </summary>
        public bool SendGearShiftVibrationIntensity(int intensity0to100)
        {
            if (!_connection.IsConnected) return false;
            if (intensity0to100 < 0) intensity0to100 = 0;
            if (intensity0to100 > 100) intensity0to100 = 100;
            ushort raw = (ushort)Math.Round(intensity0to100 / 100.0 * MaxGearShiftIntensityRaw);

            var frame = new byte[24];
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = 0x13;
            frame[2] = 0x20;
            frame[3] = MozaProtocol.DeviceAb9;
            frame[4] = 0x0A;
            frame[5] = 0x01;
            frame[6] = (byte)(raw >> 8);
            frame[7] = (byte)(raw & 0xFF);
            // frame[8..14] zero
            frame[15] = 0x0E;
            frame[16] = 0x00;
            frame[17] = 0x64;
            frame[18] = 0x04;
            // frame[19..22] zero
            frame[23] = MozaProtocol.CalculateWireChecksum(frame, 23);
            _connection.Send(frame);
            return true;
        }

        public void Dispose()
        {
            _connection.Dispose();
        }
    }
}

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Constants from the MOZA Racing serial protocol (docs/serial.md).
    /// </summary>
    public static class MozaProtocol
    {
        public const byte MessageStart = 0x7E;
        public const byte MagicValue = 0x0D; // 13 decimal, used for checksum
        public const int BaudRate = 115200;
        public const int VendorId = 0x346E; // Gudsen/Moza

        // Device IDs
        // Note: Main and Hub share ID 18 (same physical controller).
        // HPattern and Sequential share ID 26 (same device type).
        // The response parser disambiguates via group range checks.
        public const byte DeviceMain = 18;
        public const byte DeviceBase = 19;
        public const byte DeviceDash = 20;
        public const byte DeviceWheel = 23;
        public const byte DevicePedals = 25;
        public const byte DeviceHPattern = 26;
        public const byte DeviceSequential = 26;
        public const byte DeviceHandbrake = 27;
        public const byte DeviceEStop = 28;
        public const byte DeviceHub = 18;

        // Read request groups
        public const byte BaseReadSettings = 40;   // FFB, angle, etc.
        public const byte BaseReadTelemetry = 43;   // Temps, state
        public const byte PedalsReadSettings = 35;
        public const byte PedalsReadOutput = 37;    // Throttle/brake/clutch output
        public const byte WheelRead = 64;
        public const byte DashRead = 51;
        public const byte HubRead = 100;
        public const byte HandbrakeRead = 91;

        // Write request groups
        public const byte BaseWriteSettings = 41;
        public const byte BaseWriteCalibration = 42;
        public const byte BaseSendTelemetry = 65;
        public const byte PedalsWriteSettings = 36;
        public const byte WheelWrite = 63;
        public const byte DashWrite = 50;
        public const byte HandbrakeWrite = 92;

        // Dashboard telemetry (pithouse-re.md § 4)
        public const byte TelemetrySendGroup = 0x43;  // Group for telemetry data frames
        public const byte TelemetryModeGroup = 0x40;  // Group for telemetry mode config (28:02)

        public static byte CalculateChecksum(byte[] data)
        {
            int sum = MagicValue;
            for (int i = 0; i < data.Length; i++)
                sum += data[i];
            return (byte)(sum % 256);
        }

        /// <summary>
        /// Calculate checksum over the first <paramref name="length"/> bytes of <paramref name="data"/>.
        /// Useful for patching the checksum in a pre-allocated frame buffer.
        /// </summary>
        public static byte CalculateChecksum(byte[] data, int length)
        {
            int sum = MagicValue;
            for (int i = 0; i < length; i++)
                sum += data[i];
            return (byte)(sum % 256);
        }

        public static byte SwapNibbles(byte b)
        {
            return (byte)(((b & 0x0F) << 4) | ((b & 0xF0) >> 4));
        }

        public static byte ToggleBit7(byte b)
        {
            return (byte)(b ^ 0x80);
        }

        /// <summary>
        /// Commands the wheel echoes back verbatim (group | 0x80, device nibble-swapped,
        /// payload mirrored). Mirrors sim/wheel_sim.py:_WHEEL_ECHO_PREFIXES.
        /// Match form: (group, device, payload-prefix bytes). Used to short-circuit
        /// unmatched-response logging and treat echoes as wheel keepalive signals
        /// for commands not in MozaCommandDatabase.
        /// </summary>
        public static readonly byte[][] WheelEchoPrefixes = new[]
        {
            new byte[] { 0x3F, 0x17, 0x1f, 0x00 }, // per-LED color page 0
            new byte[] { 0x3F, 0x17, 0x1f, 0x01 }, // per-LED color page 1
            new byte[] { 0x3F, 0x17, 0x1e, 0x00 }, // channel CC enable page 0
            new byte[] { 0x3F, 0x17, 0x1e, 0x01 }, // channel CC enable page 1
            new byte[] { 0x3F, 0x17, 0x1b, 0x00 }, // brightness page 0
            new byte[] { 0x3F, 0x17, 0x1b, 0x01 }, // brightness page 1
            new byte[] { 0x3F, 0x17, 0x1c, 0x00 }, // page config
            new byte[] { 0x3F, 0x17, 0x1d, 0x00 },
            new byte[] { 0x3F, 0x17, 0x1d, 0x01 },
            new byte[] { 0x3F, 0x17, 0x27, 0x00 }, // LED display config page 0
            new byte[] { 0x3F, 0x17, 0x27, 0x01 },
            new byte[] { 0x3F, 0x17, 0x27, 0x02 },
            new byte[] { 0x3F, 0x17, 0x27, 0x03 },
            new byte[] { 0x3F, 0x17, 0x2a, 0x00 },
            new byte[] { 0x3F, 0x17, 0x2a, 0x01 },
            new byte[] { 0x3F, 0x17, 0x2a, 0x02 },
            new byte[] { 0x3F, 0x17, 0x2a, 0x03 },
            new byte[] { 0x3F, 0x17, 0x0a, 0x00 },
            new byte[] { 0x3F, 0x17, 0x24, 0xff }, // display setting
            new byte[] { 0x3F, 0x17, 0x20, 0x01 },
            new byte[] { 0x3F, 0x17, 0x1a, 0x00 }, // RPM LED telemetry write
            new byte[] { 0x3F, 0x17, 0x19, 0x00 }, // RPM LED color write
            new byte[] { 0x3F, 0x17, 0x19, 0x01 }, // button LED color write
            new byte[] { 0x3E, 0x17, 0x0b },       // newer-wheel LED cmd (1-byte prefix)
        };

        /// <summary>
        /// Returns true when <paramref name="data"/> is a wheel echo of a known write
        /// command. <paramref name="data"/> layout: [responseGroup, responseDeviceId, payload...].
        /// responseGroup is bit7-toggled (0xBF ↔ 0x3F, 0xBE ↔ 0x3E); responseDeviceId is
        /// nibble-swapped (0x71 ↔ 0x17). Match is against the normalized group/device.
        /// </summary>
        public static bool IsWheelEcho(byte[] data)
        {
            if (data == null || data.Length < 4) return false;
            byte group = ToggleBit7(data[0]);
            byte device = SwapNibbles(data[1]);
            foreach (var prefix in WheelEchoPrefixes)
            {
                if (prefix[0] != group || prefix[1] != device) continue;
                int prefixLen = prefix.Length - 2;
                if (data.Length < 2 + prefixLen) continue;
                bool match = true;
                for (int i = 0; i < prefixLen; i++)
                {
                    if (data[2 + i] != prefix[2 + i]) { match = false; break; }
                }
                if (match) return true;
            }
            return false;
        }
    }
}

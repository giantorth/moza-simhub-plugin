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
    }
}

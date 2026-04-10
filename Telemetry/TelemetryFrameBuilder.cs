using System;
using System.Collections.Generic;
using GameReaderCommon;
using MozaPlugin.Protocol;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Assembles a complete Moza telemetry serial frame from game data.
    ///
    /// Frame format (pithouse-re.md § 4):
    ///   7E [N] 43 17 7D 23 32 00 23 32 [flag] 20 [data...] [checksum]
    /// </summary>
    public class TelemetryFrameBuilder
    {
        private readonly DashboardProfile _profile;

        public TelemetryFrameBuilder(DashboardProfile profile)
        {
            _profile = profile;
        }

        public DashboardProfile Profile => _profile;

        /// <summary>Build frame from live game data.</summary>
        public byte[] BuildFrame(StatusDataBase? gameData, byte flagByte) =>
            BuildFrameFromSnapshot(GameDataSnapshot.FromStatusData(gameData), flagByte);

        /// <summary>Build frame from a pre-populated snapshot (test patterns, etc.).</summary>
        public byte[] BuildFrameFromSnapshot(GameDataSnapshot snapshot, byte flagByte)
        {
            // 1. Bit-pack channel values
            int bufBytes = Math.Max(1, _profile.TotalBytes);
            var writer = new TelemetryBitWriter(bufBytes);

            foreach (var ch in _profile.Channels)
            {
                double value = snapshot.GetField(ch.SimHubField);

                if (TelemetryEncoder.IsFloat(ch.Compression))
                    writer.WriteFloat((float)value);
                else if (TelemetryEncoder.IsDouble(ch.Compression))
                    writer.WriteDouble(value);
                else
                    writer.WriteBits(TelemetryEncoder.Encode(ch.Compression, value), ch.BitWidth);
            }

            byte[] data = writer.GetBuffer();

            // 2. Build frame
            // payload = cmdId(2) + header(6) + data bytes
            int payloadLen = 2 + 6 + data.Length;
            var frame = new List<byte>(4 + payloadLen + 1)
            {
                MozaProtocol.MessageStart,           // 7E
                (byte)payloadLen,                    // N
                MozaProtocol.TelemetrySendGroup,     // 43
                MozaProtocol.DeviceWheel,            // 17
                0x7D, 0x23,                          // cmdId
                0x32, 0x00, 0x23, 0x32,              // header prefix
                flagByte,                            // flag (wheel ignores value)
                0x20,                                // hardcoded constant
            };
            frame.AddRange(data);
            frame.Add(MozaProtocol.CalculateChecksum(frame.ToArray()));

            return frame.ToArray();
        }
    }
}

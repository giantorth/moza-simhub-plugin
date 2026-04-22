using System;
using GameReaderCommon;
using MozaPlugin.Protocol;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Assembles a complete Moza telemetry serial frame from game data.
    ///
    /// Frame format (moza-protocol.md § Main real-time telemetry):
    ///   7E [N] 43 17 7D 23 32 00 23 32 [flag] 20 [data...] [checksum]
    ///
    /// Header is 12 bytes fixed, followed by variable data, followed by 1 checksum byte.
    /// </summary>
    public class TelemetryFrameBuilder
    {
        private const int HeaderLen = 12; // start(1) + N(1) + group(1) + dev(1) + cmdId(2) + prefix(4) + flag(1) + const(1)
        private const int ChecksumLen = 1;

        private readonly DashboardProfile _profile;
        private readonly Func<GameDataSnapshot, double>[] _resolvers;

        // Pre-allocated buffers reused every frame to avoid GC pressure
        private readonly byte[] _frameBuffer;
        private readonly TelemetryBitWriter? _bitWriter;

        public TelemetryFrameBuilder(DashboardProfile profile)
            : this(profile, propertyResolver: null) { }

        /// <summary>
        /// Build a frame builder. If <paramref name="propertyResolver"/> is supplied,
        /// channels with a non-empty <see cref="ChannelDefinition.SimHubProperty"/>
        /// read their value via that resolver. Channels with an empty property fall
        /// back to <see cref="GameDataSnapshot.GetField"/>.
        /// </summary>
        public TelemetryFrameBuilder(DashboardProfile profile, Func<string, double>? propertyResolver)
        {
            _profile = profile;

            int dataLen = profile.TotalBytes;
            _frameBuffer = new byte[HeaderLen + dataLen + ChecksumLen];

            // Bind one resolver per channel. Per-frame cost is one delegate invoke
            // instead of a dictionary lookup.
            _resolvers = new Func<GameDataSnapshot, double>[profile.Channels.Count];
            for (int i = 0; i < profile.Channels.Count; i++)
            {
                var ch = profile.Channels[i];
                if (!string.IsNullOrEmpty(ch.SimHubProperty) && propertyResolver != null)
                {
                    var path = ch.SimHubProperty;
                    var resolver = propertyResolver;
                    _resolvers[i] = _ => resolver(path);
                }
                else
                {
                    var field = ch.SimHubField;
                    _resolvers[i] = s => s.GetField(field);
                }
            }

            // Write the static header bytes once
            _frameBuffer[0] = MozaProtocol.MessageStart;       // 7E
            _frameBuffer[1] = (byte)(2 + 6 + dataLen);         // N = cmdId(2) + header(6) + data
            _frameBuffer[2] = MozaProtocol.TelemetrySendGroup;  // 43
            _frameBuffer[3] = MozaProtocol.DeviceWheel;         // 17
            _frameBuffer[4] = 0x7D;                             // cmdId[0]
            _frameBuffer[5] = 0x23;                             // cmdId[1]
            _frameBuffer[6] = 0x32;                             // header prefix
            _frameBuffer[7] = 0x00;
            _frameBuffer[8] = 0x23;
            _frameBuffer[9] = 0x32;
            // [10] = flagByte — patched per call
            _frameBuffer[11] = 0x20;                            // hardcoded constant

            if (dataLen > 0)
                _bitWriter = new TelemetryBitWriter(_frameBuffer, HeaderLen, dataLen);
        }

        public DashboardProfile Profile => _profile;

        /// <summary>Build frame from live game data.</summary>
        public byte[] BuildFrame(StatusDataBase? gameData, byte flagByte) =>
            BuildFrameFromSnapshot(GameDataSnapshot.FromStatusData(gameData), flagByte);

        /// <summary>Build frame from a pre-populated snapshot (test patterns, etc.).</summary>
        public byte[] BuildFrameFromSnapshot(GameDataSnapshot snapshot, byte flagByte)
        {
            _frameBuffer[10] = flagByte;

            if (_bitWriter != null)
            {
                _bitWriter.Reset();

                for (int i = 0; i < _profile.Channels.Count; i++)
                {
                    var ch = _profile.Channels[i];
                    double value = _resolvers[i](snapshot);

                    if (TelemetryEncoder.IsFloat(ch.Compression))
                        _bitWriter.WriteFloat((float)value);
                    else if (TelemetryEncoder.IsDouble(ch.Compression))
                        _bitWriter.WriteDouble(value);
                    else
                        _bitWriter.WriteBits(TelemetryEncoder.Encode(ch.Compression, value), ch.BitWidth);
                }
            }

            _frameBuffer[_frameBuffer.Length - 1] = MozaProtocol.CalculateWireChecksum(
                _frameBuffer, _frameBuffer.Length - 1);

            // Return a copy: the write queue holds a reference until the write thread drains it,
            // and we reuse _frameBuffer on the next tick. One Array.Copy is still far cheaper
            // than the old List<byte> + two ToArray() allocations.
            var copy = new byte[_frameBuffer.Length];
            Array.Copy(_frameBuffer, 0, copy, 0, copy.Length);
            return copy;
        }

        /// <summary>
        /// Build a stub frame for a tier with no active channels.
        /// Frame contains the full fixed header but no data bytes.
        /// </summary>
        public static byte[] BuildStubFrame(byte flagByte)
        {
            var frame = new byte[HeaderLen + ChecksumLen];
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = (byte)(2 + 6);  // N with no data
            frame[2] = MozaProtocol.TelemetrySendGroup;
            frame[3] = MozaProtocol.DeviceWheel;
            frame[4] = 0x7D;
            frame[5] = 0x23;
            frame[6] = 0x32;
            frame[7] = 0x00;
            frame[8] = 0x23;
            frame[9] = 0x32;
            frame[10] = flagByte;
            frame[11] = 0x20;
            frame[12] = MozaProtocol.CalculateWireChecksum(frame, 12);
            return frame;
        }
    }
}

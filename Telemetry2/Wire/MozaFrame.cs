using System;
using MozaPlugin.Protocol;

namespace MozaPlugin.Telemetry2.Wire
{
    // Wraps a session-chunk body into a full MOZA serial frame:
    //
    //   7E [N] [group] [device] [body...] [checksum]
    //
    // Where N = body.Length (count starts AFTER group+device, per the wire convention
    // confirmed by Telemetry/TierDefinitionBuilder.ChunkMessage). Checksum =
    // MozaProtocol.CalculateWireChecksum (handles 0x7E byte-stuff doubling).
    //
    // Body is the output of SessionChunk.ToBodyBytes() — the 7C 00 prefix + session +
    // type + seq + payload + CRC32 trailer. The framer adds only the outer wrapper.
    //
    // group / device default to TelemetrySendGroup (0x43) / DeviceWheel (0x17), which is
    // what every observed h2b session-data frame uses. Other targets (e.g. AB9) would
    // override via the explicit overload.
    public static class MozaFrame
    {
        public static byte[] Wrap(byte[] body)
            => Wrap(body, MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel);

        public static byte[] Wrap(byte[] body, byte group, byte device)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            if (body.Length > 255)
                throw new ArgumentException($"Body exceeds MOZA frame capacity (255 bytes): {body.Length}");
            // Frame layout: [start:1] [N:1] [group:1] [device:1] [body...] [checksum:1]
            // N counts the body bytes AFTER group+device — not including the grp/dev
            // bytes themselves (matches the existing ChunkMessage convention).
            int n = body.Length;
            byte[] frame = new byte[1 + 1 + 2 + body.Length + 1];
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = (byte)n;
            frame[2] = group;
            frame[3] = device;
            Buffer.BlockCopy(body, 0, frame, 4, body.Length);
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }
    }
}

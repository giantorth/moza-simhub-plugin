using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MozaPlugin.Protocol;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Builds the tier definition message that Pithouse sends to the wheel via
    /// 7c:00 session data on the telemetry session. Tells the wheel firmware how
    /// to decode the bit-packed telemetry on each flag byte.
    ///
    /// Generic TLV layout — every top-level tag is `[tag:1B][param:u32 LE][data:param bytes]`:
    ///
    ///   [0x00] [01 00 00 00] [enable_flag:1B]                — per-tier enable (repeated)
    ///   [0x01] [size: u32 LE] [flag_byte:1B] [channels]      — tier header + channel table
    ///     [ch_index:u32] [comp_code:u32] [bits:u32] [reserved:u32]   — 16B per channel
    ///   [0x06] [04 00 00 00] [total_channels: u32 LE]        — end marker
    ///
    /// Preamble tags (0x07 protocol version, 0x03 base flag offset) use the same TLV
    /// format but are sent as a SEPARATE 14-byte 7c:00 message before this one — see
    /// TelemetrySender.SendTierDefinition. A parser scanning the session buffer must
    /// therefore treat unknown top-level tags as generic TLV and skip by param_size.
    ///
    /// In ProtocolVersion=3 (two-batch) the plugin sends two messages back-to-back
    /// on the same session: a probe batch (BuildProbeBatch, ends with total_channels=0)
    /// followed by the real tier def. A robust reassembler must not break on the first
    /// 0x06 end marker — the second message starts immediately after it.
    ///
    /// Channel indices are 1-based, assigned alphabetically by URL across all tiers,
    /// so indices within any single tier are NOT consecutive when a channel's URL
    /// sorts between two channels of a different tier. Compression codes are
    /// firmware-internal IDs mapped from type name strings.
    /// </summary>
    public static class TierDefinitionBuilder
    {
        /// <summary>
        /// Maps compression type name → firmware numeric code.
        /// Confirmed codes from F1 dashboard USB capture analysis.
        /// Unknown types get code 0xFFFF (the wheel may ignore them).
        /// </summary>
        private static readonly Dictionary<string, uint> CompressionCodes =
            new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["bool"]            = 0x00,
            ["uint3"]           = 0x14,
            ["int30"]           = 0x0D,
            ["uint30"]          = 0x0D,  // same encoding as int30
            ["uint31"]          = 0x0D,  // same encoding as int30
            ["uint8"]           = 0x01,  // inferred
            ["uint8_t"]         = 0x01,  // inferred
            ["int8_t"]          = 0x02,  // inferred
            ["float_001"]       = 0x17,
            ["percent_1"]       = 0x0E,
            ["tyre_pressure_1"] = 0x10,  // inferred (same as uint16_t for 12-bit packing?)
            ["tyre_temp_1"]     = 0x11,  // inferred
            ["track_temp_1"]    = 0x12,  // inferred
            ["oil_pressure_1"]  = 0x13,  // inferred
            ["uint15"]          = 0x03,  // inferred
            ["uint16_t"]        = 0x04,
            ["int16_t"]         = 0x05,  // inferred
            ["float_6000_1"]    = 0x0F,
            ["float_600_2"]     = 0x15,  // inferred
            ["brake_temp_1"]    = 0x16,  // inferred
            ["float"]           = 0x07,
            ["int32_t"]         = 0x08,  // inferred
            ["uint32_t"]        = 0x09,  // inferred
            ["double"]          = 0x0A,  // inferred
            ["location_t"]      = 0x0B,  // inferred
        };

        /// <summary>
        /// Build the complete tier definition message bytes from a MultiStreamProfile.
        /// This is the reassembled payload that gets chunked into 7c:00 frames.
        /// </summary>
        public static byte[] BuildTierDefinitionMessage(MultiStreamProfile profile, byte flagBase)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            // Assign 1-based channel indices alphabetically across ALL tiers
            var allChannels = profile.Tiers
                .SelectMany(t => t.Channels)
                .OrderBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var channelIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < allChannels.Count; i++)
                channelIndexMap[allChannels[i].Url] = i + 1; // 1-based

            // Tier enable entries — observed: one per tier with value=1
            // Pithouse sends enables for flag offsets 0 and 1 (regardless of tier count)
            int enableCount = Math.Max(2, profile.Tiers.Count);
            for (int i = 0; i < enableCount; i++)
            {
                w.Write((byte)0x00);         // tag
                w.Write((uint)1);            // value = 1 (enabled)
                w.Write((byte)i);            // flag offset
            }

            // Tier definitions
            for (int i = 0; i < profile.Tiers.Count; i++)
            {
                var tier = profile.Tiers[i];
                byte flag = (byte)(flagBase + i);
                int numChannels = tier.Channels.Count;
                uint size = (uint)(1 + numChannels * 16); // flag byte + 16 per channel

                w.Write((byte)0x01);         // tag
                w.Write(size);               // size (LE)
                w.Write(flag);               // flag byte for this tier

                foreach (var ch in tier.Channels) // already sorted alphabetically
                {
                    int chIndex;
                    if (!channelIndexMap.TryGetValue(ch.Url, out chIndex)) chIndex = 0;
                    uint compCode;
                    if (!CompressionCodes.TryGetValue(ch.Compression, out compCode)) compCode = 0xFFFF;
                    w.Write((uint)chIndex);   // channel index (LE)
                    w.Write(compCode);        // compression code (LE)
                    w.Write((uint)ch.BitWidth); // bit width (LE)
                    w.Write((uint)0);         // reserved
                }
            }

            // End marker
            w.Write((byte)0x06);             // tag
            w.Write((uint)4);               // param (always 4 in captures)
            w.Write((uint)allChannels.Count); // total channel count

            return ms.ToArray();
        }

        /// <summary>
        /// Build a version 0 (URL-based) subscription message.
        /// The host sends channel URLs; the wheel firmware resolves compression internally.
        /// Format (confirmed from CSP captures and VGS incoming channel catalog):
        ///   [0xFF]                                         — sentinel
        ///   [0x03] [04 00 00 00] [01 00 00 00]            — config (value=1)
        ///   [0x04] [size: u32 LE] [ch_index: u8] [url: ASCII]  — per-channel (repeated)
        ///   [0x06] [04 00 00 00] [total_channels: u32 LE] — end marker
        /// </summary>
        public static byte[] BuildV0UrlSubscription(MultiStreamProfile profile)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            var allChannels = profile.Tiers
                .SelectMany(t => t.Channels)
                .OrderBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Sentinel
            w.Write((byte)0xFF);

            // Config: tag 0x03, param_size=4, value=1 (version 0 uses value=1)
            w.Write((byte)0x03);
            w.Write((uint)4);
            w.Write((uint)1);

            // Per-channel URL entries
            for (int i = 0; i < allChannels.Count; i++)
            {
                byte[] urlBytes = System.Text.Encoding.ASCII.GetBytes(allChannels[i].Url);
                uint size = (uint)(1 + urlBytes.Length);

                w.Write((byte)0x04);
                w.Write(size);
                w.Write((byte)(i + 1)); // 1-based channel index
                w.Write(urlBytes);
            }

            // End marker
            w.Write((byte)0x06);
            w.Write((uint)4);
            w.Write((uint)allChannels.Count);

            return ms.ToArray();
        }

        /// <summary>
        /// Build a probe batch: empty tier definitions at flagBase + no enables +
        /// end marker with total_channels=0. Pithouse sends this as a first batch
        /// on the telemetry session BEFORE the real tier definitions. The probe
        /// primes the wheel's tier parser — total_channels=0 is a sentinel ("probe"),
        /// not a literal channel count. The two messages (probe + real) arrive on
        /// the same session, each ending with its own 0x06 end marker.
        /// </summary>
        public static byte[] BuildProbeBatch(MultiStreamProfile profile, byte flagBase)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            // Tier definitions with no channel data — just flag + empty channel list
            for (int i = 0; i < profile.Tiers.Count; i++)
            {
                byte flag = (byte)(flagBase + i);
                w.Write((byte)0x01);         // tag
                w.Write((uint)1);            // size = 1 (flag byte only, no channels)
                w.Write(flag);               // flag byte
            }

            // End marker with total_channels=0
            w.Write((byte)0x06);
            w.Write((uint)4);
            w.Write((uint)0);

            return ms.ToArray();
        }

        /// <summary>
        /// Chunk a message into 7c:00 session data frames ready to send.
        /// Each chunk: session(1) + type(1) + seq(2 LE) + payload(≤54 net + 4 CRC) inside a moza frame.
        /// ALL chunks have a 4-byte CRC-32 trailer (verified by CRC computation against
        /// every chunk in moza-startup-1 and moza-startup-2 captures, including final chunks).
        /// </summary>
        public static List<byte[]> ChunkMessage(byte[] message, byte session, ref int seq)
        {
            const int MaxNetPerChunk = 54;  // 58 total - 4 CRC

            var frames = new List<byte[]>();
            int offset = 0;

            while (offset < message.Length)
            {
                int remaining = message.Length - offset;
                int chunkSize = Math.Min(remaining, MaxNetPerChunk);

                var payload = new byte[chunkSize + 4]; // ALL chunks get CRC-32 trailer
                Array.Copy(message, offset, payload, 0, chunkSize);

                {
                    uint crc = Crc32(message, offset, chunkSize);
                    payload[chunkSize]     = (byte)(crc & 0xFF);
                    payload[chunkSize + 1] = (byte)((crc >> 8) & 0xFF);
                    payload[chunkSize + 2] = (byte)((crc >> 16) & 0xFF);
                    payload[chunkSize + 3] = (byte)((crc >> 24) & 0xFF);
                }

                // Build the moza frame: 7E [N] 43 17 7C 00 [session] [type=01] [seq LE] [payload] [checksum]
                int n = 2 + 1 + 1 + 2 + payload.Length; // cmd(2) + session(1) + type(1) + seq(2) + payload
                var frame = new byte[4 + n + 1]; // start(1) + N(1) + group(1) + device(1) + n_payload + checksum(1)
                frame[0] = MozaProtocol.MessageStart;
                frame[1] = (byte)n;
                frame[2] = MozaProtocol.TelemetrySendGroup;
                frame[3] = MozaProtocol.DeviceWheel;
                frame[4] = 0x7C;
                frame[5] = 0x00;
                frame[6] = session;
                frame[7] = 0x01; // type = data
                frame[8] = (byte)(seq & 0xFF);
                frame[9] = (byte)((seq >> 8) & 0xFF);
                Array.Copy(payload, 0, frame, 10, payload.Length);
                frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame);

                frames.Add(frame);
                offset += chunkSize;
                seq++;
            }

            return frames;
        }

        /// <summary>
        /// Standard CRC-32 (ISO 3309 / zlib / Ethernet).
        /// Polynomial 0xEDB88320 (reflected), init 0xFFFFFFFF, xor-out 0xFFFFFFFF.
        /// </summary>
        public static uint Crc32(byte[] data, int offset, int length)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = offset; i < offset + length; i++)
            {
                crc ^= data[i];
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            return crc ^ 0xFFFFFFFF;
        }
    }
}

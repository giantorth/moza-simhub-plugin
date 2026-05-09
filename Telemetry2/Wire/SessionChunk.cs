using System;
using System.Collections.Generic;

namespace MozaPlugin.Telemetry2.Wire
{
    public enum SessionChunkType : byte
    {
        Data = 0x01,
        Close = 0x00,
        Open = 0x81,
    }

    // Single 7c:00 session chunk. Wire format (inside a MOZA frame body):
    //   [0x7C] [0x00] [session:u8] [type:u8] [seq:u16LE] [payload...] [crc32:u32LE]
    //
    // Per Telemetry/TierDefinitionBuilder.ChunkMessage:
    //  - Payload + 4-byte CRC32 trailer fits in <= 58 bytes (so payload <= 54).
    //  - CRC32 is computed over the unescaped logical payload (NOT including CRC bytes).
    //
    // This type only emits chunks; framing into MOZA-level [7E][N][grp][dev]...[chk] is
    // done by the existing Protocol/MozaProtocol.CalculateWireChecksum + caller. Telemetry2
    // re-uses the existing framing primitive rather than re-implementing it.
    public readonly struct SessionChunk
    {
        public const int MaxPayloadBytes = 54;
        public const int HeaderBytes = 6;   // 7C 00 ses typ seq_lo seq_hi
        public const int CrcTrailerBytes = 4;

        public byte Session { get; }
        public SessionChunkType Type { get; }
        public ushort Seq { get; }
        public byte[] Payload { get; }

        public SessionChunk(byte session, SessionChunkType type, ushort seq, byte[] payload)
        {
            if (payload != null && payload.Length > MaxPayloadBytes)
                throw new ArgumentException($"payload {payload.Length} > {MaxPayloadBytes}", nameof(payload));
            Session = session;
            Type = type;
            Seq = seq;
            Payload = payload ?? Array.Empty<byte>();
        }

        // Body bytes excluding the outer MOZA frame: 7C 00 ses typ seq[lo,hi] payload crc32.
        public byte[] ToBodyBytes()
        {
            int payloadLen = Payload.Length;
            byte[] body = new byte[HeaderBytes + payloadLen + CrcTrailerBytes];
            body[0] = 0x7C;
            body[1] = 0x00;
            body[2] = Session;
            body[3] = (byte)Type;
            body[4] = (byte)(Seq & 0xFF);
            body[5] = (byte)((Seq >> 8) & 0xFF);
            if (payloadLen > 0) Array.Copy(Payload, 0, body, HeaderBytes, payloadLen);

            // CRC32 over Payload only (matches Telemetry/TierDefinitionBuilder.ChunkMessage).
            uint crc = Crc32.Compute(Payload, 0, payloadLen);
            int crcOff = HeaderBytes + payloadLen;
            body[crcOff] = (byte)(crc & 0xFF);
            body[crcOff + 1] = (byte)((crc >> 8) & 0xFF);
            body[crcOff + 2] = (byte)((crc >> 16) & 0xFF);
            body[crcOff + 3] = (byte)((crc >> 24) & 0xFF);
            return body;
        }

        // Split a logical message into a list of SessionChunks with monotonically
        // advancing seq starting from seqStart. Each chunk gets at most MaxPayloadBytes.
        // Returns the seq value to use after the last chunk.
        public static (List<SessionChunk> chunks, ushort nextSeq) ChunkMessage(
            byte[] message, byte session, ushort seqStart)
        {
            var chunks = new List<SessionChunk>();
            ushort seq = seqStart;
            int offset = 0;
            int total = message?.Length ?? 0;
            byte[] msg = message ?? Array.Empty<byte>();
            while (offset < total)
            {
                int take = Math.Min(MaxPayloadBytes, total - offset);
                byte[] payload = new byte[take];
                Array.Copy(msg, offset, payload, 0, take);
                chunks.Add(new SessionChunk(session, SessionChunkType.Data, seq, payload));
                seq++;
                offset += take;
            }
            return (chunks, seq);
        }
    }
}

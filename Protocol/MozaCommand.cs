using System;
using System.Collections.Generic;

namespace MozaTelemetryPlugin.Protocol
{
    public class MozaCommand
    {
        public string Name { get; }
        public string DeviceType { get; }
        public byte ReadGroup { get; }
        public byte WriteGroup { get; }
        public byte[] CommandId { get; }
        public int PayloadBytes { get; }
        public string PayloadType { get; }

        public MozaCommand(string name, string deviceType, byte readGroup, byte writeGroup,
            byte[] commandId, int payloadBytes, string payloadType)
        {
            Name = name;
            DeviceType = deviceType;
            ReadGroup = readGroup;
            WriteGroup = writeGroup;
            CommandId = commandId;
            PayloadBytes = payloadBytes;
            PayloadType = payloadType;
        }

        public byte[]? BuildReadMessage(byte deviceId)
        {
            if (ReadGroup == 0xFF) // -1 means not readable
                return null;

            int payloadLength = CommandId.Length + PayloadBytes;
            var msg = new List<byte>
            {
                MozaProtocol.MessageStart,
                (byte)payloadLength,
                ReadGroup,
                deviceId
            };
            msg.AddRange(CommandId);

            // Read payload: for "int" type, value is 1 (big-endian).
            // For "array" type, value is all-zeros (bytes(length)).
            var readPayload = new byte[PayloadBytes];
            if (PayloadType != "array" && readPayload.Length > 0)
                readPayload[readPayload.Length - 1] = 0x01;
            msg.AddRange(readPayload);

            msg.Add(MozaProtocol.CalculateChecksum(msg.ToArray()));
            return msg.ToArray();
        }

        public byte[]? BuildWriteMessage(byte deviceId, byte[] payload)
        {
            if (WriteGroup == 0xFF)
                return null;

            int payloadLength = CommandId.Length + payload.Length;
            var msg = new List<byte>
            {
                MozaProtocol.MessageStart,
                (byte)payloadLength,
                WriteGroup,
                deviceId
            };
            msg.AddRange(CommandId);
            msg.AddRange(payload);
            msg.Add(MozaProtocol.CalculateChecksum(msg.ToArray()));
            return msg.ToArray();
        }

        /// <summary>
        /// Convenience: build a write message with an integer value, encoded big-endian.
        /// </summary>
        public byte[]? BuildWriteInt(byte deviceId, int value)
        {
            var payload = new byte[PayloadBytes];
            for (int i = PayloadBytes - 1; i >= 0; i--)
            {
                payload[i] = (byte)(value & 0xFF);
                value >>= 8;
            }
            return BuildWriteMessage(deviceId, payload);
        }

        public static int ParseIntValue(byte[] data, int byteCount)
        {
            if (data == null || data.Length < byteCount)
                return 0;

            int value = 0;
            for (int i = 0; i < byteCount; i++)
                value = (value << 8) | data[i];
            return value;
        }

        public static float ParseFloatValue(byte[] data)
        {
            if (data == null || data.Length < 4)
                return 0f;

            // Big-endian to little-endian conversion
            var bytes = new byte[4];
            bytes[0] = data[3];
            bytes[1] = data[2];
            bytes[2] = data[1];
            bytes[3] = data[0];
            return BitConverter.ToSingle(bytes, 0);
        }
    }
}

using System;

namespace MozaPlugin.Protocol
{
    public struct ParsedResponse
    {
        public string Name;
        public int IntValue;
        public byte[] ArrayValue;
        public byte DeviceId;
    }

    /// <summary>
    /// Parses response messages from Moza devices, matching them to known commands.
    /// Matches against both ReadGroup and WriteGroup (for write confirmations).
    /// Filters out firmware debug noise (group 0x0E from main device).
    /// </summary>
    public class MozaResponseParser
    {
        /// <summary>
        /// Returns null for unrecognized messages, false for filtered noise.
        /// </summary>
        public static ParsedResponse? Parse(byte[] data)
        {
            if (data == null || data.Length < 3)
                return null;

            byte responseGroup = data[0];
            byte responseDeviceId = data[1];
            var payload = new byte[data.Length - 2];
            Array.Copy(data, 2, payload, 0, payload.Length);

            byte group = MozaProtocol.ToggleBit7(responseGroup);
            byte deviceId = MozaProtocol.SwapNibbles(responseDeviceId);

            // Filter firmware debug output (group 0x8E -> toggled 0x0E = 14, from main device 18)
            // These are unsolicited status/log messages, not protocol responses.
            if (responseGroup == 0x0E)
                return null;

            // Device hint overrides based on group range
            string? deviceHint = null;
            if (group >= 63 && group <= 66)
                deviceHint = "wheel";
            if (group == 228 || group == 100)
            {
                deviceHint = "hub";
                group = 100;
            }

            foreach (var kvp in MozaCommandDatabase.Commands)
            {
                var cmd = kvp.Value;

                // Match against ReadGroup or WriteGroup
                bool groupMatch = (cmd.ReadGroup != 0xFF && cmd.ReadGroup == group)
                               || (cmd.WriteGroup != 0xFF && cmd.WriteGroup == group);
                if (!groupMatch)
                    continue;

                if (deviceHint != null && cmd.DeviceType != deviceHint)
                    continue;

                if (payload.Length < cmd.CommandId.Length)
                    continue;

                bool idMatch = true;
                for (int i = 0; i < cmd.CommandId.Length; i++)
                {
                    if (cmd.CommandId[i] != 0xFF && payload[i] != cmd.CommandId[i])
                    {
                        idMatch = false;
                        break;
                    }
                }

                if (!idMatch)
                    continue;

                var valueData = new byte[payload.Length - cmd.CommandId.Length];
                Array.Copy(payload, cmd.CommandId.Length, valueData, 0, valueData.Length);

                var result = new ParsedResponse { Name = kvp.Key, DeviceId = deviceId };

                if (cmd.PayloadType == "array")
                {
                    result.ArrayValue = valueData;
                    result.IntValue = MozaCommand.ParseIntValue(valueData, Math.Min(valueData.Length, 4));
                }
                else if (cmd.PayloadType == "float")
                {
                    result.IntValue = (int)MozaCommand.ParseFloatValue(valueData);
                }
                else
                {
                    result.IntValue = MozaCommand.ParseIntValue(valueData, cmd.PayloadBytes);
                }

                return result;
            }

            return null;
        }
    }
}

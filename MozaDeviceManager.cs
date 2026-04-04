using System;
using MozaPlugin.Protocol;


namespace MozaPlugin
{
    /// <summary>
    /// Handles reading and writing settings to Moza devices.
    /// Includes wheel ID cycling to support different wheel models (23, 21, 19).
    /// </summary>
    public class MozaDeviceManager
    {
        private readonly MozaSerialConnection _connection;

        // Wheel device ID cycling
        // ES wheels may be on ID 21 instead of 23
        private byte _wheelDeviceId = MozaProtocol.DeviceWheel; // starts at 23
        private bool _wheelDetected;

        public byte WheelDeviceId => _wheelDeviceId;

        public MozaDeviceManager(MozaSerialConnection connection)
        {
            _connection = connection;
        }

        // Valid wheel device IDs to try (23 → 21 → 19)
        // ID 19 (base) is needed for ES wheels on the R5 where the wheel shares the base ID
        private static readonly byte[] WheelIdCandidates = { 23, 21, 19 };
        private int _wheelIdIndex;

        /// <summary>
        /// Cycle the wheel device ID through candidates: 23 → 21 → 19 → 23 → ...
        /// Called when wheel detection probes get no response.
        /// </summary>
        public void CycleWheelId()
        {
            if (_wheelDetected) return;

            _wheelIdIndex = (_wheelIdIndex + 1) % WheelIdCandidates.Length;
            _wheelDeviceId = WheelIdCandidates[_wheelIdIndex];
            SimHub.Logging.Current.Info($"[Moza] Cycling wheel ID to {_wheelDeviceId}");
        }

        public void OnWheelDetected()
        {
            _wheelDetected = true;
            SimHub.Logging.Current.Info($"[Moza] Wheel locked on device ID {_wheelDeviceId}");
        }

        public bool ReadSetting(string commandName)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildReadMessage(GetDeviceId(cmd.DeviceType));
            if (msg == null) return false;
            _connection.Send(msg);
            return true;
        }

        public bool WriteSetting(string commandName, int value)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildWriteInt(GetDeviceId(cmd.DeviceType), value);
            if (msg == null) return false;
            _connection.Send(msg);
            return true;
        }

        public bool WriteFloat(string commandName, float value)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildWriteFloat(GetDeviceId(cmd.DeviceType), value);
            if (msg == null) return false;
            _connection.Send(msg);
            return true;
        }

        public bool WriteArray(string commandName, byte[] payload)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildWriteMessage(GetDeviceId(cmd.DeviceType), payload);
            if (msg == null) return false;
            _connection.Send(msg);
            return true;
        }

        public bool WriteColor(string commandName, byte r, byte g, byte b)
        {
            return WriteArray(commandName, new byte[] { r, g, b });
        }

        public void ReadSettings(params string[] commandNames)
        {
            foreach (var name in commandNames)
                ReadSetting(name);
        }

        private byte GetDeviceId(string deviceType)
        {
            switch (deviceType)
            {
                case "base":   return MozaProtocol.DeviceBase;
                case "pedals": return MozaProtocol.DevicePedals;
                case "wheel":  return _wheelDeviceId;
                case "dash":   return MozaProtocol.DeviceDash;
                case "hub":       return MozaProtocol.DeviceHub;
                case "main":      return MozaProtocol.DeviceMain;
                case "handbrake": return MozaProtocol.DeviceHandbrake;
                default:          return MozaProtocol.DeviceBase;
            }
        }
    }
}

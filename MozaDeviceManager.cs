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

        // Wheel device ID detection
        // ES wheels may be on ID 21 instead of 23; R5 ES wheels share base ID 19
        private byte _wheelDeviceId = MozaProtocol.DeviceWheel; // starts at 23
        private bool _wheelDetected;

        public byte WheelDeviceId => _wheelDeviceId;

        public MozaDeviceManager(MozaSerialConnection connection)
        {
            _connection = connection;
        }

        // Valid wheel device IDs to try (23, 21, 19)
        private static readonly byte[] WheelIdCandidates = { 23, 21, 19 };

        /// <summary>
        /// Send detection probes for all candidate wheel IDs simultaneously.
        /// Much faster than cycling through IDs one at a time (~2s vs ~12s worst case).
        /// </summary>
        public void ProbeWheelDetection()
        {
            if (_wheelDetected) return;

            foreach (var id in WheelIdCandidates)
            {
                ReadSettingForDevice("wheel-telemetry-mode", id);
                ReadSettingForDevice("wheel-rpm-value1", id);
            }
        }

        /// <summary>
        /// Lock the wheel device ID to the one that actually responded.
        /// Called when a wheel detection probe gets a valid response.
        /// </summary>
        public void LockWheelId(byte deviceId)
        {
            if (_wheelDetected) return;
            _wheelDetected = true;
            _wheelDeviceId = deviceId;
            SimHub.Logging.Current.Info($"[Moza] Wheel locked on device ID {_wheelDeviceId}");
        }

        public bool ReadSettingForDevice(string commandName, byte deviceId)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildReadMessage(deviceId);
            if (msg == null) return false;
            _connection.Send(msg);
            return true;
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

namespace MozaPlugin.Devices
{
    internal static class MozaDeviceConstants
    {
        /// <summary>
        /// StandardDeviceId used in the .shdevicetemplate.
        /// At runtime, SimHub may use this directly or as part of the DeviceTypeID.
        /// </summary>
        public const string WheelStandardDeviceId = "MozaRacingWheel";

        public const int RpmLedCount = 10;
        public const int ButtonLedCount = 14;
        public const int FlagLedCount = 6;
    }
}

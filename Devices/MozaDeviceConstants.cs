namespace MozaPlugin.Devices
{
    internal static class MozaDeviceConstants
    {
        /// <summary>
        /// StandardDeviceId used in the .shdevicetemplate.
        /// At runtime, SimHub may use this directly or as part of the DeviceTypeID.
        /// </summary>
        public const string WheelStandardDeviceId = "MozaRacingWheel";
        public const string DashStandardDeviceId = "MozaRacingDash";

        public const int RpmLedCount = 10;
        public const int ButtonLedCount = 14;
        public const int FlagLedCount = 6;

        /// <summary>10 RPM + 6 flag LEDs, mapped as a single 16-LED strip in SimHub.</summary>
        public const int DashLedCount = 16;
    }
}

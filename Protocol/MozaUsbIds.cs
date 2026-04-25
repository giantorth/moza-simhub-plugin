using System;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// MOZA Racing USB Product IDs under VID 0x346E. Discovered via WMI during
    /// serial port enumeration; lets the plugin pick the right COM port when
    /// multiple MOZA composite devices are attached (e.g. wheelbase + AB9 shifter).
    ///
    /// PIDs are reported by ExtractPid as "0x" + 4-hex-digit uppercase, so all
    /// comparisons here are against that canonical form.
    /// </summary>
    public static class MozaUsbIds
    {
        public const string PidWheelbaseR9  = "0x0006";
        public const string PidWheelbaseR12 = "0x0002"; // KS Pro / R12 — verified via pithouse capture
        public const string PidAb9Shifter   = "0x1000";

        public static bool IsAb9Pid(string? pid)
        {
            return string.Equals(pid, PidAb9Shifter, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsWheelbasePid(string? pid)
        {
            return string.Equals(pid, PidWheelbaseR9, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pid, PidWheelbaseR12, StringComparison.OrdinalIgnoreCase);
        }
    }
}

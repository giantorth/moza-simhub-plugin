using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Security;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Process-wide MOZA USB serial port discovery via the Windows registry.
    /// Reads the standard <c>usbser.sys</c> layout
    /// <c>HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_346E&amp;PID_xxxx&amp;MI_00\&lt;instance&gt;\Device Parameters\PortName</c>
    /// and cross-references each result against <see cref="SerialPort.GetPortNames"/>
    /// so registry "ghost" entries (records left behind from a prior USB-port
    /// attachment) are filtered out.
    ///
    /// Replaces the prior WMI reflection path that failed silently in SimHub's
    /// plugin AppDomain — see <c>docs/DEVELOPMENT.md</c> for context. The serial
    /// probe fallback in <see cref="MozaSerialConnection"/> still exists but is
    /// gated behind the user-visible <c>ScanUnknownSerialPorts</c> setting; this
    /// class is the always-on primary discovery path on Windows.
    /// </summary>
    public sealed class MozaPortDiscovery
    {
        public static MozaPortDiscovery Instance { get; } = new MozaPortDiscovery();

        public const ushort MozaVid = 0x346E;

        private const string EnumUsbPath = @"SYSTEM\CurrentControlSet\Enum\USB";

        // Cache TTL — short enough to pick up plug/unplug between reconnect
        // ticks (5 s), long enough that wheelbase + AB9 managers running
        // back-to-back on the same tick share one registry walk.
        private static readonly long CacheTtlTicks = Stopwatch.Frequency * 2L;

        public readonly struct PortInfo
        {
            public readonly string PortName;     // e.g. "COM5"
            public readonly ushort Vid;          // 0x346E
            public readonly ushort Pid;          // 0x1000
            public readonly string FriendlyName; // "USB Serial Device (COM5)"
            public readonly string InstanceId;   // "a&399b951f&0&0000"

            public PortInfo(string portName, ushort vid, ushort pid, string friendlyName, string instanceId)
            {
                PortName = portName;
                Vid = vid;
                Pid = pid;
                FriendlyName = friendlyName ?? string.Empty;
                InstanceId = instanceId ?? string.Empty;
            }
        }

        private readonly object _cacheLock = new object();
        private long _cacheTimestamp;                                  // 0 = uninitialised
        private IReadOnlyList<PortInfo> _cachedPorts = Array.Empty<PortInfo>();
        private string _lastSummary = "(not yet enumerated)";
        private int _hasLoggedFirstSuccess; // 0 or 1, atomic

        private MozaPortDiscovery() { }

        /// <summary>
        /// Return the current set of MOZA CDC ACM ports visible via the
        /// registry. Cached for <see cref="CacheTtlTicks"/>; concurrent callers
        /// past the TTL may each walk the registry once — the result is
        /// idempotent so the redundant work is harmless.
        /// </summary>
        public IReadOnlyList<PortInfo> Enumerate()
        {
            lock (_cacheLock)
            {
                long now = Stopwatch.GetTimestamp();
                if (_cacheTimestamp != 0 && (now - _cacheTimestamp) < CacheTtlTicks)
                    return _cachedPorts;
            }

            var ports = EnumerateFromRegistry();
            var summary = SummarizePorts(ports);

            lock (_cacheLock)
            {
                _cachedPorts = ports;
                _cacheTimestamp = Stopwatch.GetTimestamp();
                _lastSummary = summary;
            }

            // First successful enumeration logs at Info so the user sees one
            // line in their support-bundle log confirming detection worked.
            // Subsequent enumerations log at Debug to avoid flooding.
            if (ports.Count > 0 && Interlocked.Exchange(ref _hasLoggedFirstSuccess, 1) == 0)
                MozaLog.Info($"[Moza] MOZA detection: source=registry, {summary}");
            else
                MozaLog.Debug($"[Moza] MOZA detection: source=registry, {summary}");

            return ports;
        }

        /// <summary>
        /// Convenience wrapper around <see cref="Enumerate"/> that returns only
        /// ports whose PID satisfies <paramref name="pidFilter"/>. Null filter
        /// returns all ports.
        /// </summary>
        public IReadOnlyList<PortInfo> EnumerateMatching(Func<ushort, bool>? pidFilter)
        {
            var all = Enumerate();
            if (pidFilter == null) return all;
            var matched = new List<PortInfo>(all.Count);
            for (int i = 0; i < all.Count; i++)
                if (pidFilter(all[i].Pid)) matched.Add(all[i]);
            return matched;
        }

        public bool TryGetByPort(string portName, out PortInfo info)
        {
            info = default;
            if (string.IsNullOrEmpty(portName)) return false;
            var all = Enumerate();
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i].PortName, portName, StringComparison.OrdinalIgnoreCase))
                {
                    info = all[i];
                    return true;
                }
            }
            return false;
        }

        /// <summary>Force the next <see cref="Enumerate"/> call to re-walk the registry.</summary>
        public void Invalidate()
        {
            lock (_cacheLock)
            {
                _cacheTimestamp = 0;
                _cachedPorts = Array.Empty<PortInfo>();
            }
        }

        /// <summary>Human-readable single-line summary of the most recent enumeration. UI binding.</summary>
        public string LastEnumerationSummary
        {
            get { lock (_cacheLock) return _lastSummary; }
        }

        private static IReadOnlyList<PortInfo> EnumerateFromRegistry()
        {
            // Live COM port set — drops ghost registry entries left by previous
            // USB-port attachments. SerialPort.GetPortNames reads the same
            // SERIALCOMM table the kernel populates with currently-mounted ports.
            HashSet<string> liveCom;
            try
            {
                liveCom = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[Moza] SerialPort.GetPortNames failed: {ex.GetType().Name}: {ex.Message}");
                liveCom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var results = new List<PortInfo>();
            try
            {
                using var enumKey = Registry.LocalMachine.OpenSubKey(EnumUsbPath, writable: false);
                if (enumKey == null)
                {
                    MozaLog.Debug($"[Moza] Registry: {EnumUsbPath} not found");
                    return results;
                }

                foreach (var deviceKeyName in enumKey.GetSubKeyNames())
                {
                    if (!TryParseMozaCdcKey(deviceKeyName, out var pid))
                        continue;

                    using var deviceKey = enumKey.OpenSubKey(deviceKeyName, writable: false);
                    if (deviceKey == null) continue;

                    foreach (var instanceName in deviceKey.GetSubKeyNames())
                    {
                        using var instanceKey = deviceKey.OpenSubKey(instanceName, writable: false);
                        if (instanceKey == null) continue;

                        // Only accept the standard usbser CDC driver — guards
                        // against future MOZA devices that bind a different
                        // service (WinUSB, custom driver) and shouldn't be
                        // treated as a serial pipe.
                        var service = instanceKey.GetValue("Service") as string;
                        if (!string.Equals(service, "usbser", StringComparison.OrdinalIgnoreCase))
                            continue;

                        using var paramsKey = instanceKey.OpenSubKey("Device Parameters", writable: false);
                        if (paramsKey == null) continue;

                        var portName = paramsKey.GetValue("PortName") as string;
                        if (string.IsNullOrEmpty(portName)) continue;

                        // Filter ghosts: PortName is in the registry but the
                        // COM is not currently mounted.
                        if (!liveCom.Contains(portName!)) continue;

                        var friendly = (instanceKey.GetValue("FriendlyName") as string) ?? string.Empty;
                        results.Add(new PortInfo(portName!, MozaVid, pid, friendly, instanceName));
                    }
                }
            }
            catch (SecurityException ex)
            {
                MozaLog.Debug($"[Moza] Registry access denied: {ex.Message}");
            }
            catch (IOException ex)
            {
                MozaLog.Debug($"[Moza] Registry IO failure: {ex.Message}");
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[Moza] Registry enumeration failed: {ex.GetType().Name}: {ex.Message}");
            }

            return results;
        }

        // Match keys named "VID_346E&PID_xxxx&MI_00" (case-insensitive) and
        // emit the 4-hex PID. MI_00 is the CDC ACM child interface where the
        // COM port lives; sibling interfaces (MI_02 HID etc.) are skipped.
        // Composite parent keys like "VID_346E&PID_xxxx" (no MI suffix) are
        // also skipped — they have no PortName.
        private static bool TryParseMozaCdcKey(string keyName, out ushort pid)
        {
            pid = 0;
            if (string.IsNullOrEmpty(keyName)) return false;

            const string vidPrefix = "VID_346E&PID_";
            const string miSuffix = "&MI_00";
            int expectedLen = vidPrefix.Length + 4 + miSuffix.Length;
            if (keyName.Length != expectedLen) return false;
            if (string.Compare(keyName, 0, vidPrefix, 0, vidPrefix.Length,
                               StringComparison.OrdinalIgnoreCase) != 0) return false;
            int suffixStart = keyName.Length - miSuffix.Length;
            if (string.Compare(keyName, suffixStart, miSuffix, 0, miSuffix.Length,
                               StringComparison.OrdinalIgnoreCase) != 0) return false;

            var pidHex = keyName.Substring(vidPrefix.Length, 4);
            return ushort.TryParse(pidHex, NumberStyles.HexNumber,
                                   CultureInfo.InvariantCulture, out pid);
        }

        private static string SummarizePorts(IReadOnlyList<PortInfo> ports)
        {
            if (ports.Count == 0) return "ports=[]";
            var sb = new StringBuilder("ports=[");
            for (int i = 0; i < ports.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(ports[i].PortName);
                sb.Append(":0x");
                sb.Append(ports[i].Pid.ToString("X4", CultureInfo.InvariantCulture));
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}

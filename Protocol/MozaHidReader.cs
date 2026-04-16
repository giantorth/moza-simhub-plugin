using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using HidSharp;
using HidSharp.Reports;
using HidSharp.Reports.Input;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Reads physical input positions from Moza HID devices (VID 0x346E).
    /// The base reports steering/throttle/brake/clutch; the handbrake is a
    /// separate HID device. Each device with tracked usages gets its own
    /// receiver thread.
    /// </summary>
    internal sealed class MozaHidReader : IDisposable
    {
        // HID usage IDs (page << 16 | usage)
        private const uint UsageX      = 0x00010030; // GenericDesktop.X      → steering
        private const uint UsageY      = 0x00010031; // GenericDesktop.Y      → combined paddles
        private const uint UsageZ      = 0x00010032; // GenericDesktop.Z      → throttle
        private const uint UsageRx     = 0x00010033; // GenericDesktop.Rx     → right paddle
        private const uint UsageRy     = 0x00010034; // GenericDesktop.Ry     → left paddle
        private const uint UsageRz     = 0x00010035; // GenericDesktop.Rz     → brake
        private const uint UsageSlider = 0x00010036; // GenericDesktop.Slider → clutch   (kernel: ABS_THROTTLE)
        private const uint UsageDial   = 0x00010037; // GenericDesktop.Dial   → handbrake (Wine maps ABS_RUDDER here)
        private const uint UsageSimRud = 0x000200BA; // Simulation.Rudder     → handbrake (native Windows)

        private static readonly uint[] TrackedUsages = { UsageX, UsageY, UsageZ, UsageRx, UsageRy, UsageRz, UsageSlider, UsageDial, UsageSimRud };

        private static readonly Regex HandbrakePattern = new Regex(@"hbp handbrake", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Device name patterns (lowercased), matching boxflat/foxblat's MozaHidDevice class
        private static readonly string[] DevicePatterns =
        {
            @"gudsen (moza )?r[0-9]{1,2} (ultra base|base|racing wheel and pedals)",
            @"gudsen moza (srp|sr-p|crp)[0-9]? pedals",
            @"hbp handbrake",
            @"gudsen universal hub",
        };

        private readonly MozaData _data;
        private Thread? _thread;
        private volatile bool _stop;

        public MozaHidReader(MozaData data)
        {
            _data = data;
        }

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "MozaHidReader",
            };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop = true;
            try { _thread?.Join(2000); } catch { }
            _thread = null;
            _data.IsHidConnected = false;
        }

        /// <summary>
        /// Returns the current wheel position in degrees, where 0 is center
        /// and ±(maxAngleDeg/2) is full lock.
        /// </summary>
        public double GetCurrentAngleDegrees(int maxAngleDeg)
        {
            if (!_data.IsHidConnected) return 0.0;
            int min = _data.SteeringAngleRawMin;
            int max = _data.SteeringAngleRawMax;
            int range = max - min;
            if (range <= 0) return 0.0;
            int raw = _data.SteeringAngleRaw;
            double normalized = ((raw - min) / (double)range) * 2.0 - 1.0;
            return normalized * (maxAngleDeg / 2.0);
        }

        private void Run()
        {
            while (!_stop)
            {
                var devices = FindMozaDevices();
                if (devices.Count == 0)
                {
                    _data.IsHidConnected = false;
                    SleepInterruptible(2000);
                    continue;
                }

                var threads = new List<Thread>();
                var streams = new List<HidStream>();

                try
                {
                    foreach (var (device, usages) in devices)
                    {
                        if (!device.TryOpen(out HidStream stream)) continue;
                        streams.Add(stream);

                        bool isHandbrake = HandbrakePattern.IsMatch(device.GetFriendlyName() ?? "");
                        var t = new Thread(() => ReadDevice(device, stream, usages, isHandbrake))
                        {
                            IsBackground = true,
                            Name = $"MozaHid_{device.ProductID:X4}",
                        };
                        threads.Add(t);
                        t.Start();
                    }

                    if (streams.Count > 0)
                        _data.IsHidConnected = true;

                    // Wait until stop or all threads exit
                    foreach (var t in threads)
                    {
                        while (!_stop && t.IsAlive)
                            SleepInterruptible(250);
                    }
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Info($"[Moza] HID error: {ex.Message}");
                }
                finally
                {
                    foreach (var s in streams)
                        try { s.Dispose(); } catch { }
                }

                _data.IsHidConnected = false;
                SleepInterruptible(1000);
            }
        }

        /// <summary>
        /// Finds all Moza HID devices by matching product name against known
        /// patterns (same regex as boxflat's MozaHidDevice). Returns devices
        /// that expose at least one tracked axis usage.
        /// </summary>
        private static List<(HidDevice device, Dictionary<uint, (int min, int max)> usages)> FindMozaDevices()
        {
            var result = new List<(HidDevice, Dictionary<uint, (int min, int max)>)>();
            IEnumerable<HidDevice> allDevices;
            try
            {
                allDevices = DeviceList.Local.GetHidDevices();
            }
            catch { return result; }

            foreach (var dev in allDevices)
            {
                try
                {
                    string name = (dev.GetFriendlyName() ?? "").ToLowerInvariant();
                    if (!DevicePatterns.Any(p => Regex.IsMatch(name, p)))
                        continue;

                    var usages = new Dictionary<uint, (int min, int max)>();
                    var descriptor = dev.GetReportDescriptor();
                    foreach (var item in descriptor.DeviceItems)
                    {
                        foreach (var report in item.InputReports)
                        {
                            foreach (var dataItem in report.DataItems)
                            {
                                foreach (uint usage in dataItem.Usages.GetAllValues())
                                {
                                    if (Array.IndexOf(TrackedUsages, usage) >= 0 || (usage >> 16) == 0x0009)
                                        usages[usage] = (dataItem.LogicalMinimum, dataItem.LogicalMaximum);
                                }
                            }
                        }
                    }
                    if (usages.Count > 0)
                        result.Add((dev, usages));
                }
                catch { }
            }

            return result;
        }

        private void ReadDevice(HidDevice device, HidStream stream, Dictionary<uint, (int min, int max)> usages, bool isHandbrake = false)
        {
            try
            {
                var descriptor = device.GetReportDescriptor();
                // Find the DeviceItem that contains our tracked usages
                DeviceItem? targetItem = null;
                foreach (var item in descriptor.DeviceItems)
                {
                    foreach (var report in item.InputReports)
                    {
                        foreach (var dataItem in report.DataItems)
                        {
                            if (dataItem.Usages.GetAllValues().Any(u => usages.ContainsKey(u)))
                            {
                                targetItem = item;
                                break;
                            }
                        }
                        if (targetItem != null) break;
                    }
                    if (targetItem != null) break;
                }

                if (targetItem == null) return;

                // Cache steering axis range if this device has it
                if (usages.ContainsKey(UsageX))
                {
                    _data.SteeringAngleRawMin = usages[UsageX].min;
                    _data.SteeringAngleRawMax = usages[UsageX].max;
                }

                stream.ReadTimeout = Timeout.Infinite;
                var parser = targetItem.CreateDeviceItemInputParser();
                var receiver = descriptor.CreateHidDeviceInputReceiver();
                var buffer = new byte[descriptor.MaxInputReportLength];
                var stopped = new ManualResetEventSlim(false);

                receiver.Received += (sender, e) =>
                {
                    try
                    {
                        while (receiver.TryRead(buffer, 0, out Report report))
                        {
                            if (!parser.TryParseReport(buffer, 0, report))
                                continue;

                            while (parser.HasChanged)
                            {
                                int changedIndex = parser.GetNextChangedIndex();
                                var value = parser.GetValue(changedIndex);
                                uint usage = value.Usages.FirstOrDefault();
                                if (usage == 0 || !usages.ContainsKey(usage)) continue;

                                if (usage == UsageX)
                                {
                                    _data.SteeringAngleRaw = value.GetLogicalValue();
                                }
                                else if ((usage >> 16) == 0x0009)
                                {
                                    bool pressed = value.GetLogicalValue() != 0;
                                    if (isHandbrake)
                                    {
                                        _data.HandbrakeButtonPressed = pressed;
                                    }
                                    else
                                    {
                                        int buttonIndex = (int)(usage & 0xFFFF) - 1;
                                        if (buttonIndex == 120)
                                        {
                                            _data.HandbrakeButtonPressed = pressed;
                                        }
                                        else if (buttonIndex >= 0 && buttonIndex < MozaData.MaxButtons)
                                        {
                                            _data.ButtonStates[buttonIndex] = pressed;
                                            if (buttonIndex >= _data.ButtonCount)
                                                _data.ButtonCount = buttonIndex + 1;
                                        }
                                    }
                                }
                                else
                                {
                                    var range = usages[usage];
                                    if (range.max > range.min)
                                    {
                                        int pct = NormalizePct(value.GetLogicalValue(), range.min, range.max);
                                        switch (usage)
                                        {
                                            case UsageY:      _data.CombinedPaddlePosition = pct; break;
                                            case UsageZ:      _data.ThrottlePosition       = pct; break;
                                            case UsageRx:     _data.RightPaddlePosition    = pct; break;
                                            case UsageRy:     _data.LeftPaddlePosition     = pct; break;
                                            case UsageRz:     _data.BrakePosition          = pct; break;
                                            case UsageSlider: _data.ClutchPosition         = pct; break;
                                            case UsageDial:   _data.HandbrakePosition      = pct; break;
                                            case UsageSimRud: _data.HandbrakePosition      = pct; break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                };
                receiver.Stopped += (sender, e) => stopped.Set();

                receiver.Start(stream);
                SimHub.Logging.Current.Info(
                    $"[Moza] HID device opened: {device.GetFriendlyName()} " +
                    $"(VID {device.VendorID:X4} PID {device.ProductID:X4}, " +
                    $"usages: {string.Join(", ", usages.Keys.Select(u => $"0x{u:X8}"))})");

                while (!_stop && !stopped.Wait(250)) { }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info($"[Moza] HID device read error ({device.GetFriendlyName()}): {ex.Message}");
            }
        }

        private static int NormalizePct(int raw, int min, int max)
        {
            double pct = (raw - min) / (double)(max - min) * 100.0;
            return Math.Max(0, Math.Min(100, (int)Math.Round(pct)));
        }

        private void SleepInterruptible(int ms)
        {
            int remaining = ms;
            while (remaining > 0 && !_stop)
            {
                int step = Math.Min(100, remaining);
                Thread.Sleep(step);
                remaining -= step;
            }
        }
    }
}

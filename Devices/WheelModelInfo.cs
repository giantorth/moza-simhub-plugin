using System;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Describes the physical LED layout for a specific wheel model.
    /// Used to set correct SimHub ButtonsCount, remap non-contiguous button indices,
    /// and show/hide flag LED UI based on the detected wheel.
    /// </summary>
    internal class WheelModelInfo
    {
        /// <summary>Number of physical button LEDs on this wheel.</summary>
        public int ButtonLedCount { get; }

        /// <summary>Whether this wheel has physical flag LEDs.</summary>
        public bool HasFlagLeds { get; }

        /// <summary>
        /// Maps SimHub button index (0..ButtonLedCount-1) to protocol LED index (0..13).
        /// Null when the mapping is contiguous (protocol index == SimHub index).
        /// </summary>
        public int[]? ButtonLedMap { get; }

        /// <summary>Default for unknown models — 14 buttons, no flags, contiguous.</summary>
        public static readonly WheelModelInfo Default = new(14, false, null);

        /// <summary>
        /// Known wheel models, ordered longest prefix first for correct disambiguation.
        /// Model names are 16-byte null-padded ASCII strings from the wheel firmware
        /// (group 0x07, command 0x01). Examples: "GS V2P", "CS V2.1", "VGS".
        /// </summary>
        private static readonly (string Prefix, WheelModelInfo Info)[] KnownModels =
        {
            ("GS V2P",  new WheelModelInfo(10, false, null)),
            ("CS V2.1", new WheelModelInfo(6,  false, new[] { 0, 1, 3, 6, 8, 9 })),
            ("CSP",     new WheelModelInfo(14, true,  null)),
            ("KSP",     new WheelModelInfo(14, true,  null)),
            ("FSR2",    new WheelModelInfo(14, true,  null)),
        };

        public WheelModelInfo(int buttonLedCount, bool hasFlagLeds, int[]? buttonLedMap)
        {
            ButtonLedCount = buttonLedCount;
            HasFlagLeds = hasFlagLeds;
            ButtonLedMap = buttonLedMap;
        }

        /// <summary>
        /// Resolve a wheel model info from its firmware model name string.
        /// Matches the first known prefix (longest-first ordering).
        /// Returns <see cref="Default"/> for unrecognized models.
        /// </summary>
        public static WheelModelInfo FromModelName(string modelName)
        {
            if (string.IsNullOrEmpty(modelName))
                return Default;

            foreach (var (prefix, info) in KnownModels)
            {
                if (modelName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return info;
            }

            return Default;
        }

        /// <summary>
        /// Returns true if the given 0-based protocol index has a physical button LED
        /// on this wheel model. Used by the settings UI to show/hide individual swatches.
        /// </summary>
        public bool IsButtonActive(int protocolIndex)
        {
            if (ButtonLedMap != null)
            {
                foreach (int mapped in ButtonLedMap)
                {
                    if (mapped == protocolIndex)
                        return true;
                }
                return false;
            }

            return protocolIndex >= 0 && protocolIndex < ButtonLedCount;
        }
    }
}

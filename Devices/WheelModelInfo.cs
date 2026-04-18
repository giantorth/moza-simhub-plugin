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
        /// <summary>Number of physical RPM LEDs on this wheel (center section of the LED strip).</summary>
        public int RpmLedCount { get; }

        /// <summary>Number of physical button LEDs on this wheel.</summary>
        public int ButtonLedCount { get; }

        /// <summary>
        /// Whether this wheel has 6 physical flag LEDs arranged as 3 on the left and 3
        /// on the right of the RPM strip (total physical telemetry LEDs = RpmLedCount + 6).
        /// </summary>
        public bool HasFlagLeds { get; }

        /// <summary>
        /// Maps SimHub button index (0..ButtonLedCount-1) to protocol LED index (0..13).
        /// Null when the mapping is contiguous (protocol index == SimHub index).
        /// </summary>
        public int[]? ButtonLedMap { get; }

        /// <summary>Default for unknown models — 10 RPM, 14 buttons, no flags, contiguous.</summary>
        public static readonly WheelModelInfo Default = new(10, 14, false, null);

        /// <summary>
        /// Known wheel models, ordered longest prefix first for correct disambiguation.
        /// Model names are 16-byte null-padded ASCII strings from the wheel firmware
        /// (group 0x07, command 0x01). Examples: "GS V2P", "CS V2.1", "VGS".
        /// FriendlyName is used for the SimHub device profile display name.
        /// </summary>
        internal static readonly (string Prefix, string FriendlyName, WheelModelInfo Info)[] KnownModels =
        {
            ("GS V2P",  "GS V2 Pro",  new WheelModelInfo(10, 10, false, null)),
            ("CS V2.1", "CS V2",      new WheelModelInfo(10, 6,  false, new[] { 0, 1, 3, 6, 8, 9 })),
            ("W17",     "CS Pro",     new WheelModelInfo(10, 14, true,  null)),  // firmware reports "W17" for CS Pro
            ("W18",     "KS Pro",     new WheelModelInfo(12, 14, true,  null)),  // firmware reports "W18" for KS Pro
            ("KS",      "KS",         new WheelModelInfo(10, 10, false, null)),
            ("FSR2",    "FSR V2",     new WheelModelInfo(10, 14, true,  null)),
            ("VGS",     "Vision GS",  new WheelModelInfo(10, 8,  false, null)),
            ("TSW",     "TSW",        new WheelModelInfo(10, 14, false, null)),
        };

        public WheelModelInfo(int rpmLedCount, int buttonLedCount, bool hasFlagLeds, int[]? buttonLedMap)
        {
            RpmLedCount = rpmLedCount;
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

            foreach (var (prefix, _, info) in KnownModels)
            {
                if (modelName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return info;
            }

            return Default;
        }

        /// <summary>
        /// Extract the matching known prefix from a firmware model name, or return
        /// the full model name if it doesn't match any known prefix (used as-is for
        /// device naming and GUID generation for unknown wheels).
        /// </summary>
        public static string ExtractPrefix(string modelName)
        {
            if (string.IsNullOrEmpty(modelName))
                return modelName;

            foreach (var (prefix, _, _) in KnownModels)
            {
                if (modelName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return prefix;
            }

            return modelName;
        }

        /// <summary>
        /// Get the friendly display name for a model prefix. Returns the prefix itself
        /// for unknown models.
        /// </summary>
        public static string GetFriendlyName(string modelPrefix)
        {
            foreach (var (prefix, friendlyName, _) in KnownModels)
            {
                if (prefix.Equals(modelPrefix, StringComparison.OrdinalIgnoreCase))
                    return friendlyName;
            }

            return modelPrefix;
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

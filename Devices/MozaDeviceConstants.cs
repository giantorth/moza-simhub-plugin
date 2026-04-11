using System;

namespace MozaPlugin.Devices
{
    internal static class MozaDeviceConstants
    {
        /// <summary>
        /// DescriptorUniqueId GUIDs from the .shdp device definitions.
        /// Must match the GUIDs in DeviceTemplates/*/device.json.
        /// These are permanent — changing them orphans existing user device instances.
        /// </summary>
        public const string DashGuid              = "c97a4d00-a66d-4e2f-a9b4-e7fc348dcc33";
        public const string WheelGenericGuid  = "ed153fcb-774d-4cea-97db-5f7096cd1099";
        public const string WheelOldProtoGuid = "5e70f006-ba71-4987-9e88-840d650b12ef";
        public const string WheelGSV2PGuid    = "68b2eb89-043e-4e29-be9c-4045c9636124";
        public const string WheelCSV21Guid    = "cd485bdb-934d-4d06-8224-d24fb1f82bd7";
        public const string WheelCSPGuid      = "503269ba-fc50-44d4-9844-8800da5f9f10";
        public const string WheelKSPGuid      = "14c84064-a968-43b9-ab92-a02f512632ce";
        public const string WheelFSR2Guid     = "c4f0cf35-e68c-4756-a04a-b2f8b5d6dbf3";

        /// <summary>Marker prefix returned by GetWheelModelPrefix for old-protocol devices.</summary>
        public const string OldProtocolMarker = "__old__";

        public const int RpmLedCount = 10;
        public const int ButtonLedCount = 14;
        public const int FlagLedCount = 6;

        /// <summary>
        /// GUID-to-model-prefix mapping. Used by GetWheelModelPrefix to resolve
        /// which firmware model name a device instance expects.
        /// </summary>
        private static readonly (string Guid, string Prefix)[] WheelGuidMap =
        {
            (WheelGSV2PGuid,    "GS V2P"),
            (WheelCSV21Guid,    "CS V2.1"),
            (WheelCSPGuid,      "CSP"),
            (WheelKSPGuid,      "KSP"),
            (WheelFSR2Guid,     "FSR2"),
        };

        /// <summary>
        /// Resolve the expected wheel model prefix from a SimHub DeviceTypeID.
        /// Returns null if the DeviceTypeID is not a known wheel device.
        /// Returns empty string for the generic new-protocol fallback.
        /// Returns a model prefix (e.g. "CSP") for model-specific devices.
        /// Returns <see cref="OldProtocolMarker"/> for old-protocol devices.
        /// </summary>
        public static string? GetWheelModelPrefix(string deviceTypeId)
        {
            if (string.IsNullOrEmpty(deviceTypeId))
                return null;

            // Generic new-protocol fallback
            if (Matches(deviceTypeId, WheelGenericGuid))
                return "";

            // Old-protocol device
            if (Matches(deviceTypeId, WheelOldProtoGuid))
                return OldProtocolMarker;

            // Per-model devices
            foreach (var (guid, prefix) in WheelGuidMap)
            {
                if (Matches(deviceTypeId, guid))
                    return prefix;
            }

            return null;
        }

        /// <summary>Returns true if the DeviceTypeID is a known dashboard device.</summary>
        public static bool IsDashDevice(string deviceTypeId) =>
            !string.IsNullOrEmpty(deviceTypeId) && Matches(deviceTypeId, DashGuid);

        /// <summary>Check if deviceTypeId matches an id exactly or as a prefix (for _UserProject/_Embedded suffixes).</summary>
        private static bool Matches(string deviceTypeId, string id) =>
            deviceTypeId.Equals(id, StringComparison.OrdinalIgnoreCase)
            || deviceTypeId.StartsWith(id + "_", StringComparison.OrdinalIgnoreCase);
    }
}

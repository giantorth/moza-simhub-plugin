using System;
using System.Collections.Generic;
using SimHub.Plugins.Devices;
using SimHub.Plugins.Devices.DeviceExtensions;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Tells SimHub to attach MozaWheelDeviceExtension to MOZA wheel devices.
    /// SimHub discovers this via assembly scanning for IDeviceExtensionFilter implementations.
    /// </summary>
    public class MozaDeviceExtensionFilter : IDeviceExtensionFilter
    {
        public IEnumerable<Type> GetExtensionsTypes(DeviceInstance device)
        {
            var typeId = device.DeviceDescriptor.DeviceTypeID ?? "";

            // Match by StandardDeviceId (direct or as prefix for suffixed IDs)
            if (typeId == MozaDeviceConstants.WheelStandardDeviceId
                || typeId.StartsWith(MozaDeviceConstants.WheelStandardDeviceId + "_", StringComparison.OrdinalIgnoreCase))
            {
                yield return typeof(MozaWheelDeviceExtension);
            }

            if (typeId == MozaDeviceConstants.DashStandardDeviceId
                || typeId.StartsWith(MozaDeviceConstants.DashStandardDeviceId + "_", StringComparison.OrdinalIgnoreCase))
            {
                yield return typeof(MozaDashDeviceExtension);
            }
        }
    }
}

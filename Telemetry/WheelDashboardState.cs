using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Snapshot of what the wheel reports via session 0x09 configJson RPC:
    /// which dashboards are loaded, which are disabled, canonical library
    /// names PitHouse offered. Schema matches the 2025-11 firmware capture
    /// (usb-capture/latestcaps/automobilista2-wheel-connect-dash-change.pcapng).
    /// </summary>
    public sealed class WheelDashboardState
    {
        public int TitleId { get; set; }
        public int DisplayVersion { get; set; }
        public IReadOnlyList<string> ConfigJsonList { get; set; } = Array.Empty<string>();
        public IReadOnlyList<WheelDashboardEntry> EnabledDashboards { get; set; } = Array.Empty<WheelDashboardEntry>();
        public IReadOnlyList<WheelDashboardEntry> DisabledDashboards { get; set; } = Array.Empty<WheelDashboardEntry>();
        public string RootPath { get; set; } = "";
        public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
    }

    public sealed class WheelDashboardEntry
    {
        public string Title { get; set; } = "";
        public string DirName { get; set; } = "";
        public string Hash { get; set; } = "";
        public string Id { get; set; } = "";
        public string LastModified { get; set; } = "";
    }

    /// <summary>
    /// Parses the device→host configJson state JSON. Handles BOTH firmware
    /// schemas so plugin versions work across a wheel firmware rollout:
    ///
    ///   2025-11: enableManager.dashboards[] + configJsonList + displayVersion
    ///   2026-04: enabledManager.updateDashboards[] + imagePath (top-level)
    /// </summary>
    public static class WheelStateParser
    {
        public static WheelDashboardState? Parse(byte[] jsonBytes)
        {
            try
            {
                string text = Encoding.UTF8.GetString(jsonBytes);
                var root = JObject.Parse(text);
                var state = new WheelDashboardState
                {
                    TitleId = root.Value<int?>("TitleId") ?? 0,
                    DisplayVersion = root.Value<int?>("displayVersion") ?? 0,
                };
                if (root["configJsonList"] is JArray cjl)
                {
                    var list = new List<string>();
                    foreach (var item in cjl) list.Add(item.Value<string>() ?? "");
                    state.ConfigJsonList = list;
                }
                state.EnabledDashboards = ReadDashboards(root, "enableManager", "enabledManager");
                state.DisabledDashboards = ReadDashboards(root, "disableManager", "disabledManager");
                state.RootPath = ReadRootPath(root) ?? "";
                return state;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static IReadOnlyList<WheelDashboardEntry> ReadDashboards(
            JObject root, string newKey, string oldKey)
        {
            JToken? mgr = root[newKey] ?? root[oldKey];
            if (!(mgr is JObject mgrObj)) return Array.Empty<WheelDashboardEntry>();
            JToken? arr = mgrObj["dashboards"] ?? mgrObj["updateDashboards"];
            if (!(arr is JArray jarr)) return Array.Empty<WheelDashboardEntry>();
            var items = new List<WheelDashboardEntry>();
            foreach (var d in jarr)
            {
                if (!(d is JObject o)) continue;
                items.Add(new WheelDashboardEntry
                {
                    Title = o.Value<string>("title") ?? "",
                    DirName = o.Value<string>("dirName") ?? "",
                    Hash = o.Value<string>("hash") ?? "",
                    Id = o.Value<string>("id") ?? "",
                    LastModified = o.Value<string>("lastModified") ?? "",
                });
            }
            return items;
        }

        private static string? ReadRootPath(JObject root)
        {
            foreach (var key in new[] { "enableManager", "disableManager", "enabledManager", "disabledManager" })
            {
                if (root[key] is JObject mgr && mgr.Value<string>("rootPath") is string rp)
                    return rp;
            }
            return null;
        }
    }
}

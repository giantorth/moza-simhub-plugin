using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Builds and stores dashboard profiles from bundled data and .mzdash files.
    /// </summary>
    public class DashboardProfileStore
    {
        private Dictionary<string, TelemetryChannelInfo>? _telemetryMap;
        private List<DashboardProfile>? _builtinProfiles;

        private static readonly Regex TelemetryGetRegex =
            new Regex(@"Telemetry\.get\([""'](v1/gameData/[^""']+)[""']\)",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>URL suffix → SimHub field mapping.</summary>
        private static readonly Dictionary<string, SimHubField> UrlFieldMap =
            new Dictionary<string, SimHubField>(StringComparer.OrdinalIgnoreCase)
        {
            ["SpeedKmh"]           = SimHubField.SpeedKmh,
            ["Rpm"]                = SimHubField.Rpms,
            ["Gear"]               = SimHubField.Gear,
            ["Throttle"]           = SimHubField.Throttle,
            ["Brake"]              = SimHubField.Brake,
            ["BestLapTime"]        = SimHubField.BestLapTimeSeconds,
            ["CurrentLapTime"]     = SimHubField.CurrentLapTimeSeconds,
            ["LastLapTime"]        = SimHubField.LastLapTimeSeconds,
            ["GAP"]                = SimHubField.DeltaToSessionBest,
            ["FuelRemainder"]      = SimHubField.FuelPercent,
            ["DrsState"]           = SimHubField.DrsEnabled,
            ["ErsState"]           = SimHubField.ErsPercent,
            ["TyreWearFrontLeft"]  = SimHubField.TyreWearFrontLeft,
            ["TyreWearFrontRight"] = SimHubField.TyreWearFrontRight,
            ["TyreWearRearLeft"]   = SimHubField.TyreWearRearLeft,
            ["TyreWearRearRight"]  = SimHubField.TyreWearRearRight,
        };

        public IReadOnlyList<DashboardProfile> BuiltinProfiles
        {
            get
            {
                if (_builtinProfiles == null)
                    LoadBuiltinProfiles();
                return _builtinProfiles!;
            }
        }

        private void LoadBuiltinProfiles()
        {
            _builtinProfiles = new List<DashboardProfile>();
            var assembly = Assembly.GetExecutingAssembly();

            // Each bundled .mzdash file is stored as an embedded resource
            // under MozaPlugin.Data.Dashes.<name>.mzdash
            foreach (var resourceName in assembly.GetManifestResourceNames())
            {
                if (!resourceName.EndsWith(".mzdash", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream == null) continue;
                    using var reader = new StreamReader(stream);
                    string content = reader.ReadToEnd();

                    // Derive display name from resource name
                    // e.g. MozaPlugin.Data.Dashes.Formula_1.mzdash → "Formula 1"
                    string displayName = resourceName
                        .Replace("MozaPlugin.Data.Dashes.", "")
                        .Replace(".mzdash", "")
                        .Replace("_", " ");

                    var profile = ParseMzdashContent(displayName, content);
                    if (profile != null)
                        _builtinProfiles.Add(profile);
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn($"[Moza] Failed to load builtin profile {resourceName}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Parse a .mzdash file from disk and build a profile.
        /// </summary>
        public DashboardProfile? ParseMzdash(string path, int byteLimitOverride = 0)
        {
            try
            {
                string content = File.ReadAllText(path);
                string name = Path.GetFileNameWithoutExtension(path);
                return ParseMzdashContent(name, content, byteLimitOverride);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Moza] Failed to parse .mzdash {path}: {ex.Message}");
                return null;
            }
        }

        private DashboardProfile? ParseMzdashContent(string name, string content, int byteLimitOverride = 0)
        {
            var map = GetTelemetryMap();

            // Extract all Telemetry.get() URLs, deduplicate, sort alphabetically
            var urls = TelemetryGetRegex.Matches(content)
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (urls.Count == 0)
                return null;

            return BuildProfileFromUrls(name, urls, byteLimitOverride);
        }

        /// <summary>
        /// Build a profile from a sorted list of channel URLs, applying the byte limit.
        /// </summary>
        public DashboardProfile BuildProfileFromUrls(string name, IEnumerable<string> urls,
            int byteLimitOverride = 0)
        {
            var map = GetTelemetryMap();
            var channels = new List<ChannelDefinition>();
            int totalBits = 0;

            foreach (var url in urls)
            {
                if (!map.TryGetValue(url, out var info))
                    continue;

                if (!TelemetryEncoder.BitWidths.TryGetValue(info.Compression, out int bits))
                    continue;

                // Apply byte limit: skip if adding this channel would exceed the limit
                if (byteLimitOverride > 0)
                {
                    int newBytes = (totalBits + bits + 7) / 8;
                    if (newBytes > byteLimitOverride)
                        continue;
                }

                string suffix = url.Contains('/') ? url.Substring(url.LastIndexOf('/') + 1) : url;
                UrlFieldMap.TryGetValue(suffix, out SimHubField field);

                channels.Add(new ChannelDefinition
                {
                    Name = info.Name,
                    Url = url,
                    Compression = info.Compression,
                    BitWidth = bits,
                    SimHubField = field,
                });
                totalBits += bits;
            }

            int totalBytes = (totalBits + 7) / 8;
            return new DashboardProfile
            {
                Name = name,
                Channels = channels,
                TotalBits = totalBits,
                TotalBytes = totalBytes,
            };
        }

        private Dictionary<string, TelemetryChannelInfo> GetTelemetryMap()
        {
            if (_telemetryMap == null)
                _telemetryMap = LoadTelemetryJson();
            return _telemetryMap;
        }

        private Dictionary<string, TelemetryChannelInfo> LoadTelemetryJson()
        {
            var result = new Dictionary<string, TelemetryChannelInfo>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream("MozaPlugin.Data.Telemetry.json");
                if (stream == null)
                {
                    SimHub.Logging.Current.Warn("[Moza] Telemetry.json embedded resource not found");
                    return result;
                }

                using var reader = new StreamReader(stream);
                var json = JObject.Parse(reader.ReadToEnd());
                var sectors = json["sectors"] as JArray;
                if (sectors == null) return result;

                foreach (var sector in sectors)
                {
                    string? url = sector["url"]?.ToString();
                    string? compression = sector["compression"]?.ToString();
                    string? name = sector["name"]?.ToString();
                    if (url == null || compression == null) continue;
                    result[url] = new TelemetryChannelInfo(name ?? url, compression);
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Moza] Failed to load Telemetry.json: {ex.Message}");
            }

            return result;
        }

        private struct TelemetryChannelInfo
        {
            public string Name;
            public string Compression;
            public TelemetryChannelInfo(string name, string compression)
            { Name = name; Compression = compression; }
        }
    }
}

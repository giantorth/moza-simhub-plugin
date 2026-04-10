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
    /// Builds and stores multi-stream dashboard profiles from bundled data and .mzdash files.
    /// </summary>
    public class DashboardProfileStore
    {
        private Dictionary<string, TelemetryChannelInfo>? _telemetryMap;
        private List<MultiStreamProfile>? _builtinProfiles;

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

        public IReadOnlyList<MultiStreamProfile> BuiltinProfiles
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
            _builtinProfiles = new List<MultiStreamProfile>();
            var assembly = Assembly.GetExecutingAssembly();

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
        /// Parse a .mzdash file from disk and build a multi-stream profile.
        /// </summary>
        public MultiStreamProfile? ParseMzdash(string path)
        {
            try
            {
                string content = File.ReadAllText(path);
                string name = Path.GetFileNameWithoutExtension(path);
                return ParseMzdashContent(name, content);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Moza] Failed to parse .mzdash {path}: {ex.Message}");
                return null;
            }
        }

        private MultiStreamProfile? ParseMzdashContent(string name, string content)
        {
            // Extract all Telemetry.get() URLs, deduplicate
            var urls = TelemetryGetRegex.Matches(content)
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (urls.Count == 0)
                return null;

            return BuildMultiStreamProfile(name, urls);
        }

        /// <summary>
        /// Build a MultiStreamProfile from a list of channel URLs.
        /// Channels are grouped by package_level and sorted alphabetically within each tier.
        /// Any package_level value found in Telemetry.json gets its own tier.
        /// </summary>
        public MultiStreamProfile BuildMultiStreamProfile(string name, IEnumerable<string> urls)
        {
            var map = GetTelemetryMap();
            var byLevel = new Dictionary<int, List<ChannelDefinition>>();

            foreach (var url in urls)
            {
                if (!map.TryGetValue(url, out var info))
                    continue;

                if (!TelemetryEncoder.BitWidths.TryGetValue(info.Compression, out int bits))
                    continue;

                string suffix = url.Contains('/') ? url.Substring(url.LastIndexOf('/') + 1) : url;
                UrlFieldMap.TryGetValue(suffix, out SimHubField field);

                int level = info.PackageLevel;
                if (!byLevel.ContainsKey(level))
                    byLevel[level] = new List<ChannelDefinition>();

                byLevel[level].Add(new ChannelDefinition
                {
                    Name         = info.Name,
                    Url          = url,
                    Compression  = info.Compression,
                    BitWidth     = bits,
                    SimHubField  = field,
                    PackageLevel = level,
                });
            }

            // Build tiers sorted by package_level ascending (flag offset = index)
            var tiers = byLevel.Keys
                .OrderBy(level => level)
                .Select(level => BuildTierProfile(name, byLevel[level], level))
                .ToList();

            return new MultiStreamProfile
            {
                Name  = name,
                Tiers = tiers,
            };
        }

        private static DashboardProfile BuildTierProfile(string name, List<ChannelDefinition> channels, int level)
        {
            // Sort alphabetically by URL within the tier
            var sorted = channels
                .OrderBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int totalBits = sorted.Sum(c => c.BitWidth);
            return new DashboardProfile
            {
                Name         = name,
                Channels     = sorted,
                TotalBits    = totalBits,
                TotalBytes   = (totalBits + 7) / 8,
                PackageLevel = level,
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
                    string? url         = sector["url"]?.ToString();
                    string? compression = sector["compression"]?.ToString();
                    string? name        = sector["name"]?.ToString();
                    int packageLevel    = sector["package_level"]?.Value<int>() ?? 30;

                    if (url == null || compression == null) continue;
                    result[url] = new TelemetryChannelInfo(name ?? url, compression, packageLevel);
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
            public int    PackageLevel;

            public TelemetryChannelInfo(string name, string compression, int packageLevel)
            {
                Name         = name;
                Compression  = compression;
                PackageLevel = packageLevel;
            }
        }
    }
}

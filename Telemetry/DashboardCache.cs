using System;
using System.Collections.Generic;
using System.IO;
using MozaPlugin.Diagnostics;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// On-disk + in-memory cache of parsed dashboard profiles, keyed by MD5 hash
    /// (from session 0x09 <c>enabledDashboards[].Hash</c>).
    ///
    /// Lifecycle:
    ///   1. <see cref="LoadFromDisk"/> on plugin init — reads cached .mzdash files.
    ///   2. <see cref="Ingest"/> when a dashboard is uploaded or downloaded — saves
    ///      raw mzdash content to disk, parses it, and stores the profile in memory.
    ///   3. <see cref="TryGetByName"/> during dashboard switch — looks up the
    ///      profile by dashboard name.
    /// </summary>
    public class DashboardCache
    {
        private readonly string _cacheDir;
        private readonly DashboardProfileStore _store;

        // hash → parsed profile
        private readonly Dictionary<string, MultiStreamProfile> _byHash =
            new Dictionary<string, MultiStreamProfile>(StringComparer.OrdinalIgnoreCase);

        // name → hash (populated from session 0x09 state)
        private readonly Dictionary<string, string> _nameToHash =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // hash → raw mzdash content (for upload to wheel)
        private readonly Dictionary<string, byte[]> _rawContent =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public DashboardCache(string cacheDir, DashboardProfileStore store)
        {
            _cacheDir = cacheDir;
            _store = store;
        }

        /// <summary>Number of cached profiles.</summary>
        public int Count => _byHash.Count;

        /// <summary>
        /// Scan the cache directory for previously-saved .mzdash files and parse them.
        /// File names are <c>{hash}.mzdash</c>.
        /// </summary>
        public void LoadFromDisk()
        {
            if (!Directory.Exists(_cacheDir))
            {
                try { Directory.CreateDirectory(_cacheDir); }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[Moza] DashboardCache: cannot create {_cacheDir}: {ex.Message}");
                    return;
                }
            }

            foreach (var file in Directory.GetFiles(_cacheDir, "*.mzdash"))
            {
                try
                {
                    string hash = Path.GetFileNameWithoutExtension(file);
                    if (_byHash.ContainsKey(hash)) continue;

                    string content = File.ReadAllText(file);
                    // The display name is stored as the first line if it starts with "//name:"
                    string name = hash;
                    if (content.StartsWith("//name:"))
                    {
                        int nl = content.IndexOf('\n');
                        if (nl > 0)
                        {
                            name = content.Substring(7, nl - 7).Trim();
                            content = content.Substring(nl + 1);
                        }
                    }

                    var profile = _store.ParseMzdashContent(name, content);
                    if (profile != null)
                    {
                        _byHash[hash] = profile;
                        _nameToHash[name] = hash;
                        _rawContent[hash] = System.Text.Encoding.UTF8.GetBytes(content);
                        MozaLog.Info($"[Moza] DashboardCache: loaded '{name}' from disk (hash={hash.Substring(0, 8)}...)");
                    }
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[Moza] DashboardCache: failed to load {file}: {ex.Message}");
                }
            }

            MozaLog.Info($"[Moza] DashboardCache: {_byHash.Count} profiles loaded from disk");
        }

        /// <summary>
        /// Update the name → hash mapping from session 0x09 wheel state.
        /// Call this every time a new <see cref="WheelDashboardState"/> arrives.
        /// Returns the list of hashes that are NOT in the cache (need download).
        /// </summary>
        public List<string> UpdateFromWheelState(WheelDashboardState state)
        {
            var missing = new List<string>();
            _nameToHash.Clear();

            if (state.EnabledDashboards == null) return missing;

            foreach (var dash in state.EnabledDashboards)
            {
                if (string.IsNullOrEmpty(dash.Title) || string.IsNullOrEmpty(dash.Hash))
                    continue;

                _nameToHash[dash.Title] = dash.Hash;

                if (!_byHash.ContainsKey(dash.Hash))
                    missing.Add(dash.Hash);
            }

            if (missing.Count > 0)
                MozaLog.Info($"[Moza] DashboardCache: {missing.Count} dashboards not cached, need download");

            return missing;
        }

        /// <summary>
        /// Ingest a raw mzdash file — parse it, cache in memory, and persist to disk.
        /// </summary>
        public bool Ingest(string hash, string dashboardName, string mzdashContent)
        {
            try
            {
                var profile = _store.ParseMzdashContent(dashboardName, mzdashContent);
                if (profile == null)
                {
                    MozaLog.Warn($"[Moza] DashboardCache: failed to parse mzdash for '{dashboardName}'");
                    return false;
                }

                _byHash[hash] = profile;
                _nameToHash[dashboardName] = hash;
                _rawContent[hash] = System.Text.Encoding.UTF8.GetBytes(mzdashContent);

                // Persist to disk
                try
                {
                    if (!Directory.Exists(_cacheDir))
                        Directory.CreateDirectory(_cacheDir);

                    string filePath = Path.Combine(_cacheDir, $"{hash}.mzdash");
                    // Prepend name comment for re-loading
                    File.WriteAllText(filePath, $"//name:{dashboardName}\n{mzdashContent}");
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[Moza] DashboardCache: disk write failed for '{dashboardName}': {ex.Message}");
                }

                MozaLog.Info($"[Moza] DashboardCache: ingested '{dashboardName}' (hash={hash.Substring(0, Math.Min(8, hash.Length))}...)");
                return true;
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Moza] DashboardCache: ingest failed for '{dashboardName}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Ingest raw mzdash bytes (e.g. from download or embedded resource).
        /// </summary>
        public bool Ingest(string hash, string dashboardName, byte[] mzdashBytes)
        {
            string content = System.Text.Encoding.UTF8.GetString(mzdashBytes);
            return Ingest(hash, dashboardName, content);
        }

        /// <summary>
        /// Look up a cached profile by dashboard name.
        /// Returns null if no profile is cached for this name.
        /// </summary>
        public MultiStreamProfile? TryGetByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_nameToHash.TryGetValue(name, out var hash))
            {
                if (_byHash.TryGetValue(hash, out var profile))
                    return profile;
            }
            return null;
        }

        /// <summary>
        /// Look up a cached profile by hash.
        /// </summary>
        public MultiStreamProfile? TryGetByHash(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return null;
            _byHash.TryGetValue(hash, out var profile);
            return profile;
        }

        /// <summary>
        /// Get the raw mzdash content bytes for a dashboard (for upload to wheel).
        /// </summary>
        public byte[]? TryGetRawContent(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_nameToHash.TryGetValue(name, out var hash))
            {
                if (_rawContent.TryGetValue(hash, out var content))
                    return content;
            }
            return null;
        }

        /// <summary>
        /// Check if a hash is cached.
        /// </summary>
        public bool HasHash(string hash) => _byHash.ContainsKey(hash);

        /// <summary>
        /// All cached dashboard names.
        /// </summary>
        public IEnumerable<string> CachedNames => _nameToHash.Keys;
    }
}

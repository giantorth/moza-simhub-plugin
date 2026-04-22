using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Session 0x09 configJson RPC. Device pushes a compressed JSON state blob
    /// listing enabled / disabled dashboards; host replies with a compressed
    /// `configJson()` object whose `dashboards` array is PitHouse's canonical
    /// library (dashboards the host could upload on demand).
    ///
    /// Without at least one round-trip on this session, PitHouse's Dashboard
    /// Manager UI stays empty even if mzdash uploads succeeded — the UI
    /// reads the wheel's reported state, not its own upload log.
    /// </summary>
    public sealed class ConfigJsonClient
    {
        private readonly SessionDataReassembler _deviceInbox = new();
        private WheelDashboardState? _lastState;

        /// <summary>Most recent dashboard state parsed from the device.</summary>
        public WheelDashboardState? LastState => _lastState;

        /// <summary>Feed one session 0x09 device→host chunk payload.</summary>
        public WheelDashboardState? OnChunk(byte[] chunkPayload)
        {
            _deviceInbox.AddChunk(chunkPayload);
            byte[]? decomp = _deviceInbox.TryDecompress();
            if (decomp == null) return null;
            var state = WheelStateParser.Parse(decomp, out var missing);
            if (state != null)
            {
                _lastState = state;
                // Consume the decoded blob so successive updates (e.g. after
                // a dashboard change) can reassemble a fresh one.
                _deviceInbox.Clear();
                try
                {
                    SimHub.Logging.Current.Info(
                        $"[Moza] configJson state received: TitleId={state.TitleId} " +
                        $"displayVersion={state.DisplayVersion} resetVersion={state.ResetVersion} " +
                        $"configJsonList={state.ConfigJsonList.Count} " +
                        $"enabled={state.EnabledDashboards.Count} disabled={state.DisabledDashboards.Count} " +
                        $"imageRefMap={state.ImageRefMap.Count} imagePath={state.ImagePath.Count} " +
                        $"rootDirPath='{state.RootDirPath}'");
                }
                catch { /* logging optional in unit tests */ }
                // Diagnostic: flag firmware-schema drift. 2025-11 firmware
                // emits all 11 top-level fields; older or newer firmware may
                // drop/rename fields and PitHouse's UI flatly rejects the
                // state when any are missing. Log once per distinct shape.
                if (missing.Count > 0)
                {
                    string shape = string.Join(",", missing);
                    if (shape != _lastMissingShape)
                    {
                        _lastMissingShape = shape;
                        try
                        {
                            SimHub.Logging.Current.Info(
                                $"[Moza] configJson state missing {missing.Count} expected top-level field(s): {shape}. " +
                                "Firmware may be older than 2025-11 or schema has drifted — PitHouse UI may reject.");
                        }
                        catch { /* logging optional in unit tests */ }
                    }
                }
            }
            return state;
        }

        private string _lastMissingShape = "";

        /// <summary>
        /// Build the host's `configJson()` reply. Pass the library names the
        /// host wants to expose to the wheel — PitHouse's 2025-11 capture used
        /// ["Core","Grids","Mono","Nebula","Pulse","Rally V1".."Rally V6"].
        /// The wheel echoes the list back in its next state blob's
        /// <c>configJsonList</c> field.
        /// </summary>
        public static byte[] BuildConfigJsonReply(IReadOnlyList<string> dashboards, int id = 11)
        {
            string json = BuildReplyJson(dashboards, id);
            byte[] uncompressed = Encoding.UTF8.GetBytes(json);
            byte[] compressed = CompressZlib(uncompressed);
            // Same 9-byte envelope as device→host state blobs on session 0x09:
            //   [flag:1B=0x00] [comp_size:u32 LE] [uncomp_size:u32 LE] [zlib]
            var env = new byte[9 + compressed.Length];
            env[0] = 0x00;
            uint c = (uint)compressed.Length;
            env[1] = (byte)(c & 0xFF); env[2] = (byte)((c >> 8) & 0xFF);
            env[3] = (byte)((c >> 16) & 0xFF); env[4] = (byte)((c >> 24) & 0xFF);
            uint u = (uint)uncompressed.Length;
            env[5] = (byte)(u & 0xFF); env[6] = (byte)((u >> 8) & 0xFF);
            env[7] = (byte)((u >> 16) & 0xFF); env[8] = (byte)((u >> 24) & 0xFF);
            Array.Copy(compressed, 0, env, 9, compressed.Length);
            return env;
        }

        private static string BuildReplyJson(IReadOnlyList<string> dashboards, int id)
        {
            var inner = new JObject
            {
                ["dashboardRootDir"] = "",
                ["dashboards"] = new JArray(dashboards),
                ["fontRootDir"] = "",
                ["fonts"] = new JArray(),
                ["imageRootDir"] = "",
                ["sortTags"] = 0,
            };
            var root = new JObject
            {
                ["configJson()"] = inner,
                ["id"] = id,
            };
            return root.ToString(Formatting.None);
        }

        private static byte[] CompressZlib(byte[] data)
        {
            using var output = new MemoryStream();
            output.WriteByte(0x78);
            output.WriteByte(0x9C);
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(data, 0, data.Length);
            uint adler = Adler32(data);
            output.WriteByte((byte)((adler >> 24) & 0xFF));
            output.WriteByte((byte)((adler >> 16) & 0xFF));
            output.WriteByte((byte)((adler >> 8) & 0xFF));
            output.WriteByte((byte)(adler & 0xFF));
            return output.ToArray();
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }
    }
}

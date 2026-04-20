using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using MozaPlugin.Telemetry;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MozaPlugin.Tests.Telemetry
{
    public class ConfigJsonClientTests
    {
        [Fact]
        public void BuildConfigJsonReply_ProducesDecompressibleJsonWithCorrectShape()
        {
            var canon = new List<string> { "Core", "Grids", "Rally V1" };
            byte[] env = ConfigJsonClient.BuildConfigJsonReply(canon, id: 11);
            // Envelope: [flag:1][comp:u32 LE][uncomp:u32 LE][zlib]
            Assert.Equal(0x00, env[0]);
            uint compSize = BitConverter.ToUInt32(env, 1);
            uint uncompSize = BitConverter.ToUInt32(env, 5);
            Assert.Equal((uint)env.Length - 9, compSize);
            // Decompress and check JSON
            byte[] zlib = new byte[env.Length - 9];
            Array.Copy(env, 9, zlib, 0, zlib.Length);
            byte[] uncompressed = Decompress(zlib);
            Assert.Equal(uncompSize, (uint)uncompressed.Length);
            var root = JObject.Parse(Encoding.UTF8.GetString(uncompressed));
            var inner = (JObject)root["configJson()"]!;
            var dashes = (JArray)inner["dashboards"]!;
            Assert.Equal(3, dashes.Count);
            Assert.Equal("Core", dashes[0].Value<string>());
            Assert.Equal(11, root.Value<int>("id"));
            Assert.Equal(0, inner.Value<int>("sortTags"));
        }

        [Fact]
        public void OnChunk_ParsesStateJsonFromCapturedFormat()
        {
            // Build a fake state JSON matching 2025-11 schema
            var state = new JObject
            {
                ["TitleId"] = 1,
                ["configJsonList"] = new JArray { "Core", "Rally V1" },
                ["displayVersion"] = 11,
                ["disableManager"] = new JObject
                {
                    ["dashboards"] = new JArray(),
                    ["imageRefMap"] = new JObject(),
                    ["rootPath"] = "/home/moza/resource/dashes",
                },
                ["enableManager"] = new JObject
                {
                    ["dashboards"] = new JArray
                    {
                        new JObject
                        {
                            ["title"] = "Rally V1",
                            ["dirName"] = "Rally V1",
                            ["hash"] = "deadbeef",
                            ["id"] = "uuid-1",
                            ["lastModified"] = "2025-11-21T07:45:36Z",
                        }
                    },
                    ["imageRefMap"] = new JObject(),
                    ["rootPath"] = "/home/moza/resource/dashes",
                },
            };
            byte[] uncompressed = Encoding.UTF8.GetBytes(state.ToString(Newtonsoft.Json.Formatting.None));
            byte[] compressed = Compress(uncompressed);
            // Build the 9-byte envelope + zlib stream
            byte[] blob = new byte[9 + compressed.Length];
            blob[0] = 0x00;
            uint c = (uint)compressed.Length;
            blob[1] = (byte)(c & 0xFF); blob[2] = (byte)((c >> 8) & 0xFF);
            blob[3] = (byte)((c >> 16) & 0xFF); blob[4] = (byte)((c >> 24) & 0xFF);
            uint u = (uint)uncompressed.Length;
            blob[5] = (byte)(u & 0xFF); blob[6] = (byte)((u >> 8) & 0xFF);
            blob[7] = (byte)((u >> 16) & 0xFF); blob[8] = (byte)((u >> 24) & 0xFF);
            Array.Copy(compressed, 0, blob, 9, compressed.Length);
            // Wrap with per-chunk CRC32 trailer (reassembler strips it)
            byte[] chunk = new byte[blob.Length + 4];
            Array.Copy(blob, chunk, blob.Length);
            uint crc = TierDefinitionBuilder.Crc32(blob, 0, blob.Length);
            chunk[blob.Length] = (byte)(crc & 0xFF);
            chunk[blob.Length + 1] = (byte)((crc >> 8) & 0xFF);
            chunk[blob.Length + 2] = (byte)((crc >> 16) & 0xFF);
            chunk[blob.Length + 3] = (byte)((crc >> 24) & 0xFF);

            var client = new ConfigJsonClient();
            var parsed = client.OnChunk(chunk);
            Assert.NotNull(parsed);
            Assert.Equal(1, parsed!.TitleId);
            Assert.Equal(11, parsed.DisplayVersion);
            Assert.Single(parsed.EnabledDashboards);
            Assert.Equal("Rally V1", parsed.EnabledDashboards[0].Title);
            Assert.Equal("deadbeef", parsed.EnabledDashboards[0].Hash);
        }

        [Fact]
        public void Parse_OldFirmwareSchemaAlsoWorks()
        {
            // Old 2026-04 schema: disabledManager / enabledManager / updateDashboards
            var state = new JObject
            {
                ["TitleId"] = 4,
                ["enabledManager"] = new JObject
                {
                    ["updateDashboards"] = new JArray
                    {
                        new JObject
                        {
                            ["title"] = "Formula 1",
                            ["dirName"] = "Formula 1",
                            ["hash"] = "abc123",
                        }
                    }
                }
            };
            byte[] bytes = Encoding.UTF8.GetBytes(state.ToString());
            var parsed = WheelStateParser.Parse(bytes);
            Assert.NotNull(parsed);
            Assert.Equal(4, parsed!.TitleId);
            Assert.Single(parsed.EnabledDashboards);
            Assert.Equal("Formula 1", parsed.EnabledDashboards[0].Title);
        }

        private static byte[] Compress(byte[] data)
        {
            using var output = new MemoryStream();
            output.WriteByte(0x78); output.WriteByte(0x9C);
            using (var d = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                d.Write(data, 0, data.Length);
            uint a = 1, b = 0;
            foreach (var x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
            uint adler = (b << 16) | a;
            output.WriteByte((byte)((adler >> 24) & 0xFF));
            output.WriteByte((byte)((adler >> 16) & 0xFF));
            output.WriteByte((byte)((adler >> 8) & 0xFF));
            output.WriteByte((byte)(adler & 0xFF));
            return output.ToArray();
        }

        private static byte[] Decompress(byte[] zlib)
        {
            byte[] raw = new byte[zlib.Length - 6];
            Array.Copy(zlib, 2, raw, 0, raw.Length);
            using var ms = new MemoryStream(raw);
            using var d = new DeflateStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            d.CopyTo(outMs);
            return outMs.ToArray();
        }
    }
}

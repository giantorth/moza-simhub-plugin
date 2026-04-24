using System;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry;
using Xunit;

namespace MozaPlugin.Tests.Integration
{
    /// <summary>
    /// Verifies generated frames against ground truth captured from real PitHouse sessions.
    ///
    /// Source: usb-capture/12-04-26/moza-telemetry-20260412-021744.txt (100 PitHouse frames)
    /// Game: Assetto Corsa, car idling with occasional revs, F1 dashboard ("m Formula 1.mzdash")
    /// </summary>
    public class CaptureComparisonTests
    {
        // All 100 PitHouse F1 telemetry frames from usb-capture/12-04-26/.
        // Flag byte is 0x01 throughout this session.
        // Each frame: 7E 18 43 17 7D 23 32 00 23 32 01 20 [16 data bytes] [checksum]
        private static readonly string[] PitHouseFrames = new[]
        {
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 48 5c e9 14 01 00 00 00 00 40 6a 00 00 00 00 91",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 68 5e e9 14 01 00 00 00 00 40 6a 00 00 00 00 b3",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 b4 60 e9 14 01 00 00 00 00 40 6a 00 00 00 00 01",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 d0 62 e9 14 01 00 00 00 00 40 6a 00 00 00 00 1f",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 ec 64 e9 14 01 00 00 00 00 40 6a 00 00 00 00 3d",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 0c 67 e9 14 01 00 00 00 00 40 6a 00 00 00 00 60",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 28 69 e9 14 01 00 00 00 00 40 6a 00 00 00 00 7e",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 4c 6a e9 14 01 00 00 00 00 40 6a 00 00 00 00 a3",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 6c 6c e9 14 01 00 00 00 00 40 6a 00 00 00 00 c5",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 88 6e e9 14 01 00 00 00 00 40 6a 00 00 00 00 e3",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 7c 6f e9 14 01 00 00 00 00 40 6a 00 00 00 00 d8",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 cc 71 e9 14 01 00 00 00 00 40 6a 00 00 00 00 2a",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 e8 73 e9 14 01 00 00 00 00 40 6a 00 00 00 00 48",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 04 76 e9 14 01 00 00 00 00 40 6a 00 00 00 00 67",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 20 78 e9 14 01 00 00 00 00 40 6a 00 00 00 00 85",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 3c 7a e9 14 01 00 00 00 00 40 6a 00 00 00 00 a3",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 34 7b e9 14 01 00 00 00 00 40 6a 00 00 00 00 9c",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 80 7d e9 14 01 00 00 00 00 40 6a 00 00 00 00 ea",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 9c 7f e9 14 01 00 00 00 00 40 6a 00 00 00 00 08",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 88 81 e9 14 01 00 00 00 00 40 6a 00 00 00 00 f6",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 d8 83 e9 14 01 00 00 00 00 40 6a 00 00 00 00 48",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 f4 85 e9 14 01 00 00 00 00 40 6a 00 00 00 00 66",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 10 88 e9 14 01 00 00 00 00 40 6a 00 00 00 00 85",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 10 88 e9 14 01 00 00 00 00 40 6a 00 00 00 00 85",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 54 8b e9 14 01 00 00 00 00 40 6a 00 00 00 00 cc",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 48 8c e9 14 01 00 00 00 00 40 6a 00 00 00 00 c1",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 98 8e e9 14 01 00 00 00 00 40 6a 00 00 00 00 13",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 b4 90 e9 14 01 00 00 00 00 40 6a 00 00 00 00 31",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 d0 92 e9 14 01 00 00 00 00 40 6a 00 00 00 00 4f",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 ec 94 e9 14 01 00 00 00 00 40 6a 00 00 00 00 6d",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 14 96 e9 14 01 00 00 00 00 40 6a 00 00 00 00 97",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 28 99 e9 14 01 00 00 00 00 40 6a 00 00 00 00 ae",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 4c 9a e9 14 01 00 00 00 00 40 6a 00 00 00 00 d3",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 6c 9c e9 14 01 00 00 00 00 40 6a 00 00 00 00 f5",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 88 9e e9 14 01 00 00 00 00 40 6a 00 00 00 00 13",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 a4 a0 e9 14 01 00 00 00 00 40 6a 00 00 00 00 31",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 cc a1 e9 14 01 00 00 00 00 40 6a 00 00 00 00 5a",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 e8 a3 e9 14 01 00 00 00 00 40 6a 00 00 00 00 78",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 04 a6 e9 14 01 00 00 00 00 40 6a 00 00 00 00 97",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 f0 a7 e9 14 01 00 00 00 00 40 6a 00 00 00 00 84",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 3c aa e9 14 01 00 00 00 00 40 6a 00 00 00 00 d3",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 64 ab e9 14 01 00 00 00 00 40 6a 00 00 00 00 fc",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 80 ad e9 14 01 00 00 00 00 40 6a 00 00 00 00 1a",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 9c af e9 14 01 00 00 00 00 40 6a 00 00 00 00 38",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 bc b1 e9 14 01 00 00 00 00 40 6a 00 00 00 00 5a",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 d8 b3 e9 14 01 00 00 00 00 40 6a 00 00 00 00 78",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 f4 b5 e9 14 01 00 00 00 00 40 6a 00 00 00 00 96",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 40 b8 e9 14 01 00 00 00 00 40 6a 00 00 00 00 e5",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 38 b9 e9 14 01 00 00 00 00 40 6a 00 00 00 00 de",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 60 ba e9 14 01 00 00 00 00 40 6a 00 00 00 00 07",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 48 bc e9 14 01 00 00 00 00 40 6a 00 00 00 00 f1",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 98 be e9 14 01 00 00 00 00 40 6a 00 00 00 00 43",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 b4 c0 e9 14 01 00 00 00 00 40 6a 00 00 00 00 61",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 d0 c2 e9 14 01 00 00 00 00 40 6a 00 00 00 00 7f",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 ec c4 e9 14 01 00 00 00 00 40 6a 00 00 00 00 9d",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 0c c7 e9 14 01 00 00 00 00 40 6a 00 00 00 00 c0",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 58 c9 e9 14 01 00 00 00 00 40 6a 00 00 00 00 0e",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 4c ca e9 14 01 00 00 00 00 40 6a 00 00 00 00 03",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 6c cc e9 14 01 00 00 00 00 40 6a 00 00 00 00 25",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 88 ce e9 14 01 00 00 00 00 40 6a 00 00 00 00 43",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 d4 d0 e9 14 01 00 00 00 00 40 6a 00 00 00 00 91",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 cc d1 e9 14 01 00 00 00 00 40 6a 00 00 00 00 8a",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 e8 d3 e9 14 01 00 00 00 00 40 6a 00 00 00 00 a8",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 04 d6 e9 14 01 00 00 00 00 40 6a 00 00 00 00 c7",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 f8 d6 e9 14 01 00 00 00 00 40 6a 00 00 00 00 bb",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 3c da e9 14 01 00 00 00 00 40 6a 00 00 00 00 03",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 64 db e9 14 01 00 00 00 00 40 6a 00 00 00 00 2c",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 80 dd e9 14 01 00 00 00 00 40 6a 00 00 00 00 4a",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 9c df e9 14 01 00 00 00 00 40 6a 00 00 00 00 68",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 bc e1 e9 14 01 00 00 00 00 40 6a 00 00 00 00 8a",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 d8 e3 e9 14 01 00 00 00 00 40 6a 00 00 00 00 a8",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 f4 e5 e9 14 01 00 00 00 00 40 6a 00 00 00 00 c6",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 40 e8 e9 14 01 00 00 00 00 40 6a 00 00 00 00 15",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 60 ea e9 14 01 00 00 00 00 40 6a 00 00 00 00 37",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 54 eb e9 14 01 00 00 00 00 40 6a 00 00 00 00 2c",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 7c ec e9 14 01 00 00 00 00 40 6a 00 00 00 00 55",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 98 ee e9 14 01 00 00 00 00 40 6a 00 00 00 00 73",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 b4 f0 e9 14 01 00 00 00 00 40 6a 00 00 00 00 91",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 d0 f2 e9 14 01 00 00 00 00 40 6a 00 00 00 00 af",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 ec f4 e9 14 01 00 00 00 00 40 6a 00 00 00 00 cd",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 0c f7 e9 14 01 00 00 00 00 40 6a 00 00 00 00 f0",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 28 f9 e9 14 01 00 00 00 00 40 6a 00 00 00 00 0e",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 4c fa e9 14 01 00 00 00 00 40 6a 00 00 00 00 33",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 6c fc e9 14 01 00 00 00 00 40 6a 00 00 00 00 55",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 88 fe e9 14 01 00 00 00 00 40 6a 00 00 00 00 73",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 a4 00 ea 14 01 00 00 00 00 40 6a 00 00 00 00 92",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 cc 01 ea 14 01 00 00 00 00 40 6a 00 00 00 00 bb",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 e8 03 ea 14 01 00 00 00 00 40 6a 00 00 00 00 d9",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 04 06 ea 14 01 00 00 00 00 40 6a 00 00 00 00 f8",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 20 08 ea 14 01 00 00 00 00 40 6a 00 00 00 00 16",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 3c 0a ea 14 01 00 00 00 00 40 6a 00 00 00 00 34",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 64 0b ea 14 01 00 00 00 00 40 6a 00 00 00 00 5d",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 80 0d ea 14 01 00 00 00 00 40 6a 00 00 00 00 7b",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 9c 0f ea 14 01 00 00 00 00 40 6a 00 00 00 00 99",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 bc 11 ea 14 01 00 00 00 00 40 6a 00 00 00 00 bb",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 d8 13 ea 14 01 00 00 00 00 40 6a 00 00 00 00 d9",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 f4 15 ea 14 01 00 00 00 00 40 6a 00 00 00 00 f7",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 40 18 ea 14 01 00 00 00 00 40 6a 00 00 00 00 46",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 40 18 ea 14 01 00 00 00 00 40 6a 00 00 00 00 46",
            "7e 18 43 17 7d 23 32 00 23 32 01 20 00 54 1b ea 14 01 00 00 00 00 40 6a 00 00 00 00 5d",
        };

        [Fact]
        public void AllPitHouseFrames_HaveValidChecksums()
        {
            for (int i = 0; i < PitHouseFrames.Length; i++)
            {
                byte[] frame = HexUtil.Parse(PitHouseFrames[i]);
                Assert.Equal(29, frame.Length);
                byte expected = MozaProtocol.CalculateChecksum(frame, frame.Length - 1);
                Assert.True(expected == frame[frame.Length - 1],
                    $"Frame {i}: expected checksum 0x{expected:X2}, got 0x{frame[frame.Length - 1]:X2}");
            }
        }

        [Fact]
        public void AllPitHouseFrames_ShareConsistentHeader()
        {
            byte[] expectedHeader = { 0x7E, 0x18, 0x43, 0x17, 0x7D, 0x23, 0x32, 0x00, 0x23, 0x32 };

            foreach (var hex in PitHouseFrames)
            {
                byte[] frame = HexUtil.Parse(hex);
                for (int i = 0; i < expectedHeader.Length; i++)
                    Assert.Equal(expectedHeader[i], frame[i]);

                Assert.Equal(0x01, frame[10]); // flag byte consistent in this session
                Assert.Equal(0x20, frame[11]); // hardcoded constant
            }
        }

        [Fact]
        public void PitHouseFrame_NByteEquals0x18_Means16DataBytes()
        {
            byte[] frame = HexUtil.Parse(PitHouseFrames[0]);
            // N = 0x18 = 24 = cmdId(2) + header(4) + flag+const(2) + data(16)
            Assert.Equal(0x18, frame[1]);
            Assert.Equal(29, frame.Length); // 1(start) + 1(N) + 24(payload) + 1(checksum) + 2(group+dev) = 29
        }

        [Fact]
        public void PitHouseFrame_HeaderMatchesBuilderOutput()
        {
            var profile = F1DashboardProfileFixture.BuildTier30ms();
            Assert.Equal(16, profile.TotalBytes);

            var builder = new TelemetryFrameBuilder(profile);
            byte[] generated = builder.BuildFrameFromSnapshot(default, flagByte: 0x01);
            byte[] captured = HexUtil.Parse(PitHouseFrames[0]);

            Assert.Equal(captured.Length, generated.Length);
            for (int i = 0; i < 12; i++)
                Assert.Equal(captured[i], generated[i]);
        }

        [Fact]
        public void PitHouseFrame_DecodeBrake_IsZero()
        {
            // Car is idling — Brake (float_001, bits 0-9) should be 0
            byte[] data = ExtractDataRegion(PitHouseFrames[0]);
            uint brake = ReadBits(data, 0, 10);
            Assert.Equal(0u, brake);
        }

        [Fact]
        public void PitHouseFrame_DecodeGear_IsNeutral()
        {
            // Car is idling — Gear (int30, bits 79-83) should be 0 (neutral)
            byte[] data = ExtractDataRegion(PitHouseFrames[0]);
            uint gear = ReadBits(data, 79, 5);
            Assert.Equal(0u, gear);
        }

        [Fact]
        public void PitHouseFrame_DecodeRpm_IsPlausibleIdle()
        {
            // Car is idling at constant RPM — Rpm (uint16_t, bits 84-99)
            byte[] data = ExtractDataRegion(PitHouseFrames[0]);
            uint rpm = ReadBits(data, 84, 16);
            // Idle RPM in Assetto Corsa is typically 700-2000
            Assert.InRange(rpm, 500u, 3000u);
        }

        [Fact]
        public void PitHouseFrame_DecodeRpm_ConsistentAcrossFrames()
        {
            // RPM is constant across all frames in this capture (car idling)
            byte[] data0 = ExtractDataRegion(PitHouseFrames[0]);
            uint rpm0 = ReadBits(data0, 84, 16);

            for (int i = 1; i < PitHouseFrames.Length; i++)
            {
                byte[] data = ExtractDataRegion(PitHouseFrames[i]);
                uint rpm = ReadBits(data, 84, 16);
                Assert.Equal(rpm0, rpm);
            }
        }

        [Fact]
        public void PitHouseFrame_DecodeSpeed_IsZero()
        {
            // Car is stationary — SpeedKmh (float_6000_1, bits 100-115) should be 0
            byte[] data = ExtractDataRegion(PitHouseFrames[0]);
            uint speed = ReadBits(data, 100, 16);
            Assert.Equal(0u, speed);
        }

        [Fact]
        public void PitHouseFrame_DecodeThrottle_IsZero()
        {
            // Car is idling — Throttle (float_001, bits 116-125) should be 0
            byte[] data = ExtractDataRegion(PitHouseFrames[0]);
            uint throttle = ReadBits(data, 116, 10);
            Assert.Equal(0u, throttle);
        }

        [Fact]
        public void PitHouseFrame_DecodeCurrentLapTime_IsPositiveFloat()
        {
            // CurrentLapTime (float, bits 10-41) should be a positive value (timer running)
            byte[] data = ExtractDataRegion(PitHouseFrames[0]);

            // Extract 32 bits at offset 10, reconstruct as IEEE754 LE float
            byte[] floatBytes = new byte[4];
            for (int bit = 0; bit < 32; bit++)
            {
                int srcBit = 10 + bit;
                int srcByte = srcBit / 8;
                int srcOffset = srcBit % 8;
                int dstByte = bit / 8;
                int dstOffset = bit % 8;
                if ((data[srcByte] & (1 << srcOffset)) != 0)
                    floatBytes[dstByte] |= (byte)(1 << dstOffset);
            }
            float lapTime = BitConverter.ToSingle(floatBytes, 0);
            Assert.True(lapTime > 0, $"CurrentLapTime should be positive, got {lapTime}");
        }

        [Fact]
        public void PitHouseFrame_DecodeCurrentLapTime_AdvancesOverTime()
        {
            // The lap timer should be monotonically increasing across frames
            float prev = DecodeLapTime(PitHouseFrames[0]);
            for (int i = 10; i < PitHouseFrames.Length; i += 10)
            {
                float current = DecodeLapTime(PitHouseFrames[i]);
                Assert.True(current >= prev, $"Frame {i}: lap time {current} < previous {prev}");
                prev = current;
            }
        }

        [Fact]
        public void TestPattern_RoundTrip_FrameChecksumValid()
        {
            var profile = F1DashboardProfileFixture.BuildTier30ms();
            var builder = new TelemetryFrameBuilder(profile);

            for (int frame = 0; frame < 200; frame++)
            {
                byte[] bytes = builder.BuildTestFrame(0x01);
                Assert.Equal(
                    MozaProtocol.CalculateChecksum(bytes, bytes.Length - 1),
                    bytes[bytes.Length - 1]);
            }
        }

        private static byte[] ExtractDataRegion(string frameHex)
        {
            byte[] frame = HexUtil.Parse(frameHex);
            byte[] data = new byte[16];
            Array.Copy(frame, 12, data, 0, 16);
            return data;
        }

        private static uint ReadBits(byte[] data, int bitOffset, int bitCount)
        {
            uint value = 0;
            for (int i = 0; i < bitCount; i++)
            {
                int byteIdx = (bitOffset + i) / 8;
                int bitIdx = (bitOffset + i) % 8;
                if ((data[byteIdx] & (1 << bitIdx)) != 0)
                    value |= (1u << i);
            }
            return value;
        }

        private static float DecodeLapTime(string frameHex)
        {
            byte[] data = ExtractDataRegion(frameHex);
            byte[] floatBytes = new byte[4];
            for (int bit = 0; bit < 32; bit++)
            {
                int srcBit = 10 + bit;
                int srcByte = srcBit / 8;
                int srcOffset = srcBit % 8;
                int dstByte = bit / 8;
                int dstOffset = bit % 8;
                if ((data[srcByte] & (1 << srcOffset)) != 0)
                    floatBytes[dstByte] |= (byte)(1 << dstOffset);
            }
            return BitConverter.ToSingle(floatBytes, 0);
        }
    }
}

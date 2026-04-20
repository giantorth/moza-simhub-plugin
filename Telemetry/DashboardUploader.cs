using System;
using System.IO;
using System.IO.Compression;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Orchestrates the session 0x04 file-transfer upload of a `.mzdash`
    /// dashboard file. Replaces the previous session 0x01 FF-prefixed upload
    /// that matched pre-2025-11 firmware — the current wheel firmware only
    /// acts on mzdash writes delivered via session 0x04.
    ///
    /// Produces two sub-messages (path registration + file content) built by
    /// <see cref="FileTransferBuilder"/>. The caller is responsible for the
    /// wire-level dance (wait for device's session 0x04 open, send sub-msg 1,
    /// wait for echo, send sub-msg 2, wait for ack, send end marker).
    /// </summary>
    public static class DashboardUploader
    {
        /// <summary>Bundle carrying the two sub-messages + the chosen correlation token.</summary>
        public sealed class UploadPayload
        {
            public byte[] SubMsg1PathRegistration { get; set; } = Array.Empty<byte>();
            public byte[] SubMsg2FileContent { get; set; } = Array.Empty<byte>();
            public uint Token { get; set; }
            public string DashboardName { get; set; } = "";
            public string Md5Hex { get; set; } = "";
            public int UncompressedSize { get; set; }
        }

        /// <summary>
        /// Build a session 0x04 upload for <paramref name="mzdashContent"/>.
        /// <paramref name="dashboardName"/> is used to construct the
        /// destination path (`/home/moza/resource/dashes/{name}/{name}.mzdash`)
        /// and should match the dashboard's `dirName` so PitHouse groups the
        /// upload under the correct UI entry.
        /// </summary>
        public static UploadPayload BuildUpload(byte[] mzdashContent, string dashboardName,
                                                uint token, long timestampMs)
        {
            if (mzdashContent == null) throw new ArgumentNullException(nameof(mzdashContent));
            if (string.IsNullOrEmpty(dashboardName))
                throw new ArgumentException("dashboardName required", nameof(dashboardName));
            byte[] md5 = FileTransferBuilder.ComputeMd5(mzdashContent);
            string md5Hex = FileTransferBuilder.Md5Hex(md5);
            string localTemp = FileTransferBuilder.BuildLocalTempPath(timestampMs);
            string remoteStaging = FileTransferBuilder.BuildRemoteStagingPath(md5Hex);
            string destPath = FileTransferBuilder.BuildDashboardDestPath(dashboardName);
            return new UploadPayload
            {
                SubMsg1PathRegistration = FileTransferBuilder.BuildPathRegistration(
                    localTemp, remoteStaging, md5, token),
                SubMsg2FileContent = FileTransferBuilder.BuildFileContent(
                    localTemp, remoteStaging, md5, token, destPath, mzdashContent),
                Token = token,
                DashboardName = dashboardName,
                Md5Hex = md5Hex,
                UncompressedSize = mzdashContent.Length,
            };
        }

        /// <summary>
        /// Pick a correlation token that doesn't clash with session/seq usage.
        /// PitHouse uses `0x054B` in the dash-upload capture; we use the low
        /// bits of the current tick so multiple uploads in a single session
        /// don't collide while still staying recognisable in logs.
        /// </summary>
        public static uint PickToken()
            => (uint)(Environment.TickCount ^ (int)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) & 0x7FFFFFFF;
    }
}

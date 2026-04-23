using System.Text;
using MozaPlugin.Telemetry;
using Xunit;

namespace MozaPlugin.Tests.Telemetry
{
    /// <summary>
    /// Sanity tests for the session 0x04 file-transfer wrapper in
    /// <see cref="DashboardUploader"/>. Detailed wire-format coverage lives
    /// in <see cref="FileTransferBuilderTests"/>.
    /// </summary>
    public class DashboardUploaderTests
    {
        [Fact]
        public void BuildUpload_PopulatesBothSubMessages()
        {
            byte[] mzdash = Encoding.UTF8.GetBytes("{\"name\":\"Test Dash\"}");
            var payload = DashboardUploader.BuildUpload(mzdash, "test-dash", token: 0x1234u, timestampMs: 1700000000000L);
            Assert.NotEmpty(payload.SubMsg1PathRegistration);
            Assert.NotEmpty(payload.SubMsg2FileContent);
            Assert.Equal(0x1234u, payload.Token);
            Assert.Equal("test-dash", payload.DashboardName);
            Assert.Equal(mzdash.Length, payload.UncompressedSize);
            Assert.Equal(32, payload.Md5Hex.Length);
        }

        [Fact]
        public void PickToken_ProducesPositiveInt()
        {
            uint token = DashboardUploader.PickToken();
            Assert.InRange(token, 0u, uint.MaxValue >> 1);
        }

        [Fact]
        public void BuildUpload_DifferentContent_DifferentMd5()
        {
            byte[] a = Encoding.UTF8.GetBytes("{\"name\":\"A\"}");
            byte[] b = Encoding.UTF8.GetBytes("{\"name\":\"B\"}");
            var pa = DashboardUploader.BuildUpload(a, "dash", 1u, 0L);
            var pb = DashboardUploader.BuildUpload(b, "dash", 1u, 0L);
            Assert.NotEqual(pa.Md5Hex, pb.Md5Hex);
        }
    }
}

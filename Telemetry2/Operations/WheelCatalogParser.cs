using System.Collections.Generic;
using System.Text;

namespace MozaPlugin.Telemetry2.Operations
{
    // Scans a raw byte buffer for tag=0x04 URL records and returns a 1-based
    // catalog (URL by wheel index) suitable for TierDefNegotiator.SetCatalog.
    //
    // Wire format observed in pre-Type02 firmware b2h streams (per
    // Telemetry/TelemetrySender.ParseWheelChannelCatalog:2443):
    //
    //   [0x04] [size:u32LE] [idx:u8] [url-bytes : size-1]
    //
    // Where size in [1, 200), idx is 1-based wheel-firmware-canonical index, and
    // url-bytes is ASCII (e.g. "v1/gameData/Rpms").
    //
    // Backref: size==1 (urlLen==0) means "this idx still maps to its prior URL" —
    // ignored by this parser since we lack prior catalog context. The host's
    // _catalogFromWheel flag stays sticky once set, so backref-only updates won't
    // overwrite previously-parsed URLs.
    //
    // The parser does NOT decompress FF kind=8 records. Type02 firmware embeds
    // catalog inside zlib FF kind=8 streams; this parser only handles raw tag=04
    // records that appear directly in the buffer.
    public static class WheelCatalogParser
    {
        // Scan the buffer; return a List<string?> indexed [idx-1]. Slots without a
        // valid record stay null. Caller may compact to non-null URLs in idx order.
        public static List<string?> Parse(byte[] buffer)
        {
            var catalog = new List<string?>();
            int i = 0;
            while (i + 6 < buffer.Length)
            {
                byte tag = buffer[i];
                if (tag != 0x04) { i++; continue; }
                uint size = (uint)(buffer[i + 1]
                                 | (buffer[i + 2] << 8)
                                 | (buffer[i + 3] << 16)
                                 | (buffer[i + 4] << 24));
                if (size < 1 || size >= 200 || i + 5 + (int)size > buffer.Length)
                {
                    i++;
                    continue;
                }
                int idx = buffer[i + 5];
                int urlLen = (int)size - 1;
                if (idx < 1 || idx > 200) { i++; continue; }
                if (urlLen > 0)
                {
                    int urlStart = i + 6;
                    string? url = DecodeUrl(buffer, urlStart, urlLen);
                    if (url != null)
                    {
                        EnsureSize(catalog, idx);
                        catalog[idx - 1] = url;
                    }
                }
                i += 5 + (int)size;
            }
            return catalog;
        }

        // Compact a parsed catalog into a 1-based dense list (URL by idx). Slots
        // beyond the highest-set index are dropped; nulls between are kept (caller
        // may treat as "unknown URL at this idx" — TierDefNegotiator falls back to
        // index 0 for unknowns).
        public static IReadOnlyList<string> CompactByIndex(IReadOnlyList<string?> sparse)
        {
            int last = -1;
            for (int i = sparse.Count - 1; i >= 0; i--)
            {
                if (!string.IsNullOrEmpty(sparse[i])) { last = i; break; }
            }
            if (last < 0) return System.Array.Empty<string>();
            var dense = new string[last + 1];
            for (int i = 0; i <= last; i++) dense[i] = sparse[i] ?? "";
            return dense;
        }

        // Decode a URL value from the three formats the wheel uses:
        //  1. Full:   "v1/gameData/..."  (printable ASCII, starts with v1/ or alpha)
        //  2. Prefix: 0x01 + suffix      → "v1/gameData/" + suffix
        //  3. Abbrev: 0x5C 0x31 + suffix → "v1/gameData/" + expand(suffix)
        //     Abbreviations (matching v1 TelemetrySender.ParseWheelChannelCatalog):
        //       \t → TyreTemp, \P → TyrePressure
        //       {FL} → FrontLeft, {FR} → FrontRight, {RL} → RearLeft, {RR} → RearRight
        private static string? DecodeUrl(byte[] buf, int start, int len)
        {
            if (len < 1) return null;
            byte b0 = buf[start];

            if (b0 == 0x01 && len >= 2)
            {
                string suffix = Encoding.ASCII.GetString(buf, start + 1, len - 1);
                return "v1/gameData/" + suffix;
            }

            if (b0 == 0x5C && len >= 4 && buf[start + 1] == 0x31)
            {
                string suffix = Encoding.ASCII.GetString(buf, start + 2, len - 2);
                suffix = suffix
                    .Replace("\\t", "TyreTemp")
                    .Replace("\\P", "TyrePressure")
                    .Replace("{FL}", "FrontLeft")
                    .Replace("{FR}", "FrontRight")
                    .Replace("{RL}", "RearLeft")
                    .Replace("{RR}", "RearRight");
                return "v1/gameData/" + suffix;
            }

            if (len >= 2)
            {
                byte b1 = buf[start + 1];
                bool startOk = (b0 == (byte)'v' && b1 == (byte)'1')
                            || b0 == (byte)'/'
                            || (b0 >= (byte)'a' && b0 <= (byte)'z')
                            || (b0 >= (byte)'A' && b0 <= (byte)'Z');
                if (!startOk) return null;
                for (int j = 0; j < len; j++)
                {
                    byte c = buf[start + j];
                    if (c < 0x20 || c >= 0x7F) return null;
                }
                return Encoding.ASCII.GetString(buf, start, len);
            }

            return null;
        }

        private static void EnsureSize(List<string?> list, int sizeAtLeast)
        {
            while (list.Count < sizeAtLeast) list.Add(null);
        }
    }
}

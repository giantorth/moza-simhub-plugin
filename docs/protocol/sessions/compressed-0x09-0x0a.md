### Compressed transfer format (sessions 0x09, 0x0a)

Sessions 0x09 (configJson state) and 0x0a (RPC) prepend a 9-byte header to the reassembled application data:

```
flags(1)  comp_sz+4(4 LE)  uncomp_sz(4 LE)  [zlib data...]
```

The `comp_sz` field stores the compressed byte count **plus 4** (confirmed across five reset-RPC blobs in 2026-04-21 captures — envelope `00 1d 00 00 00 11 00 00 00` for a 25-byte zlib stream and 17-byte JSON body, i.e. field=29, comp=25, uncomp=17). Zlib stream uses standard deflate (`78 9c` magic). Reassembly: strip 4-byte CRC from each chunk, concatenate payloads (excluding session/type/seq headers), then parse 9-byte header and decompress.

**Session 0x04 root directory listing does NOT use this envelope** — it uses a 53-byte prefix documented in [`../dashboard-upload/session-04-root-dir.md`](../dashboard-upload/session-04-root-dir.md). Session 0x03 tile-server uses a third format (12-byte wrapper, see [`session-0x03-tile-envelope.md`](session-0x03-tile-envelope.md)). **One envelope per session**, not shared.

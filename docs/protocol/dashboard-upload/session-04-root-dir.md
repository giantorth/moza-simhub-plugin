### Session 0x04 device → host root directory listing (2025-11 firmware)

> **2025-11 firmware.** 53-byte prefix + 176B trailing tail (not zlib). Capture: `automobilista2-wheel-connect-dash-change.pcapng`. See [`../FIRMWARE.md`](../FIRMWARE.md) for the firmware-era matrix.

Shortly after session 0x04 opens, device pushes filesystem root listing so host can see what's on wheel before choosing to re-upload. Envelope **differs from session 0x09** — a 53-byte prefix precedes the zlib stream (verified 2026-04-22 by decoding `automobilista2-wheel-connect-dash-change.pcapng`):

```
Offset  Bytes                                           Meaning
0x00    0a                                              subtype tag
0x01    <size LE:4>     (e.g. d5 00 00 00 = 213)        byte count after this field
0x05    <pathlen BE:4><0x00>  (e.g. 00 00 00 14 00)     path length in bytes + null sep
0x0a    <UTF-16LE path>  (20 B for "/home/root")        utf-16 directory path
+path   ff ff ff ff ff ff ff ff 00                      9-byte padding sentinel
+9      de c3 90 00 00 00 00 00 00 00                   10-byte unknown metadata block
+10     a9 88 01 00                                     4-byte unknown (LE 100521 — not uncomp size)
-----   zlib deflate stream of the JSON listing
```

Decoded JSON body:

```json
{"children":[{"children":[],"createTime":-28800000,"fileSize":0,"md5":"d41d8cd98f00b204e9800998ecf8427e","modifyTime":1755251038000,"name":"temp"}],"createTime":-28800000,"fileSize":0,"md5":"","modifyTime":1755251038000,"name":"root"}
```

Children nest recursively. `createTime` of `-28800000` (–8 h in ms) is UTC epoch offset marker wheel firmware ships with. Semantics of the 14-byte unknown metadata block (`de c3 90 …` + `a9 88 01 00`) are not decoded — sim emits them verbatim and PitHouse still parses the listing, so they may be header padding the wheel firmware populates but the host ignores.

After each upload (and on initial connection), wheel pushes the listing on session 0x04 using this wrapper. Plugin reassembles via second `SessionDataReassembler` instance (`_session04Inbox`), decompresses JSON, logs child count. `_session04DirListingRefreshed` flips true on each complete listing.

**Earlier doc incorrectly claimed "same 9-byte envelope as configJson" for this message.** That shape decoded to garbage for the plugin-side parser; decompression succeeded only when the 53-byte prefix was stripped first. Sim now builds this envelope correctly (see `build_session04_dir_listing` in `sim/wheel_sim.py`, 2026-04-22).

### Per-chunk metadata trailer (continuation chunks)

> **2026-04+ firmware.** Continuation-chunk format. Capture: `latestcaps/pithouse-switch-list-delete-upload-reupload.pcapng`. See [`../FIRMWARE.md`](../FIRMWARE.md) for the firmware-era matrix.

In session 0x09 (and other multi-chunk uploads with the 6B header layout), each type=0x03 sub-msg body has the same shared TLV envelope at the start, then a per-chunk variable region:

| Body offset | Bytes | Meaning |
|-------------|-------|---------|
| 0–280 | shared | LOCAL 0x8C path TLV + REMOTE 0x70 path TLV + `0x10` flag + 16B md5 + reserved + token. Identical across every chunk in one upload attempt. |
| 281–283 | 3B LE counter | 0 for the first chunk; varies per continuation (signals stream-resume position; not a strict bytes_written, but tracks PitHouse's notion of progress). |
| 284–289 | 7B constant | `03 92 16 00 00 0f fc` — observed identical in every continuation chunk and at the same offset in chunk0. |
| 290+ | varies by chunk | If counter==0: `dest_path TLV` (UTF-16BE) + `compressed_header(8)`, then `78 9c` zlib magic begin at body[≈1267]. If counter>0: raw deflate continuation starts immediately at body[290]. |

So the sim must use **two different intra-msg offsets**:
- chunk0 (counter=0, has `78 9c` magic): deflate at body[zlib_magic_offset], typically ~1267.
- chunk1+ (counter>0, no magic): deflate at body[290].

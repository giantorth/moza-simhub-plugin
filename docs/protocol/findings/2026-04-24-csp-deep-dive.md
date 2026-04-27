## Findings from 2026-04-24 deep-dive (CSP on R9)

Captures: `usb-capture/latestcaps/pithouse-switch-list-delete-upload-reupload.pcapng` (49 MB, CSP wheel on R9 base, VID/PID `346e:0002`, 2026-04 firmware) and `usb-capture/ksp/putOnWheelAndOpenPitHouse.pcapng` (KS Pro connect).

> **Canonical topical homes for facts in this journal:**
>
> | H3 section | Now also documented at |
> |------------|------------------------|
> | Short-form identity probes | [`../identity/wheel-probe-sequence.md`](../identity/wheel-probe-sequence.md) |
> | Hub (dev 0x12) and base (dev 0x13) identity cascade | [`../identity/hub-base-cascade.md`](../identity/hub-base-cascade.md) |
> | Session cumulative ACKs | [`../sessions/chunk-format.md`](../sessions/chunk-format.md) |
> | Session data chunk CRC format | [`../sessions/chunk-format.md`](../sessions/chunk-format.md) |
> | Session 1 device→host content differs from sim | [`../tier-definition/session-01-device-desc.md`](../tier-definition/session-01-device-desc.md) |
> | CSP session 2 desc chunk sizes | [`../tier-definition/session-02-channel-catalog.md`](../tier-definition/session-02-channel-catalog.md) |
> | `43:17:fc:00:*` is host-side ACK, not a probe | [`../sessions/chunk-format.md`](../sessions/chunk-format.md) |
> | Hub/base identity probes are 0x00-prefixed | [`../identity/hub-base-cascade.md`](../identity/hub-base-cascade.md) |
> | hw_id must match between session1_desc and cmd 0x06 | [`../tier-definition/session-01-device-desc.md`](../tier-definition/session-01-device-desc.md) |
> | enableManager.dashboards factory-populated on empty FS | [`../dashboard-upload/config-rpc-session-09.md`](../dashboard-upload/config-rpc-session-09.md) |
> | Session 0x06 file-transfer paths (2026-04 firmware) | [`../dashboard-upload/upload-handshake-2026-04.md`](../dashboard-upload/upload-handshake-2026-04.md) |
> | Channel catalog TLV framing | [`../tier-definition/session-02-channel-catalog.md`](../tier-definition/session-02-channel-catalog.md) |

### Short-form identity probes (grp 0x43, dev 0x17) — no sub-byte variant

PitHouse sends BOTH a sub-byte form (`43:17:08:01`) and a SHORT form (`43:17:08`) for display identity probes. Across all 23 captures analyzed, the short form is used 9–16× per capture for each of cmd `02/07/08/0f/10/11`; the sub-byte form for cmd `08/0f/10/11` appears **zero** times. Sim entries `(0x43, 0x17, b'\x08\x01')` etc. in `_build_identity_tables` are dead code; the short form `(0x43, 0x17, b'\x08')` is what gets hit on real hardware.

Short-form responses observed (byte-exact):

| Probe | Response | Notes |
|-------|----------|-------|
| `43:17:07` | `c3:71:87:01:<16B name>` | Same as long-form `07:01` |
| `43:17:08` | `c3:71:88:01:<16B hw_version>` | Sub-byte 0x01 defaulted |
| `43:17:0f` | `c3:71:8f:01:<16B sw_version>` | Sub-byte 0x01 defaulted |
| `43:17:10` | `c3:71:90:00:<16B serial0>` | Sub-byte 0x00 defaulted |
| `43:17:11` | `c3:71:91:04` | 2B not 3B; no trailing 0x01 sub |

### Hub (dev 0x12) and base (dev 0x13) identity cascade

PitHouse issues the same identity probe set (cmd `02/04/05/06/07/08/0e/0f/10/11`) to **hub** (0x12) and **base** (0x13) on top of the wheel (0x17) probes. A wheel-only sim leaves ~8000 probes unanswered per connect, and without the `R9 Black # MOT-...` / `RS21-D01-MC PB` identity responses PitHouse treats hub/base as stranger devices and never progresses Dashboard Manager to "fully detected". Hub + base JSON replay tables (`sim/replay/csp_r9_hub_12.json`, `csp_r9_base_13.json`) cover 99 + 111 entries respectively.

Grp 0x28/0x29/0x2a/0x2b also target base with scalar queries — extracted to same JSON table.

### Session cumulative ACKs — `fc:00 [sess] [seq_lo] [seq_hi]` (5-byte payload)

Both directions emit cumulative session acks:

```
7E 05 [grp] [dev] FC 00 [sess] [seq_lo] [seq_hi] [cksum]
```

Real wheel: `grp=C3 dev=71`. Host: `grp=43 dev=17`. `seq_hi` is usually `0x00` (acks fit in 1 byte). `sess` is the session byte being acked. Values don't have to match the current chunk seq — it's a cumulative window ack, so `fc:00:01:04` means "I've processed up to seq 4 on session 1".

Sim previously had this on emit (`resp_session_ack`) but had NO consumer for inbound `fc:00` from host — they fell through to the replay table which returned a stale capture-time ack that confused PitHouse's retry logic. `_handle_wheel` now consumes them silently.

### Session data chunk CRC format

All session `7c:00` data chunks carry a **4-byte CRC-32 LE** trailer on the net body. Earlier analysis (2026-04-24) reported a 3-byte truncated CRC based on offset miscounts during capture reassembly; re-verification against the same captures confirms 4 bytes. `chunk_session_payload` in `sim/wheel_sim.py` encodes chunks as `[54B net data] + [CRC32_LE:4]`, matching wire format on both firmware generations. Direct raw chunking without the CRC (earlier `_queue_file_transfer_echo` implementation) is rejected by PitHouse silently.

### Session 1 device→host content differs from sim's emit

Real CSP wheel emits on session 0x01 device→host (direction: wheel→host):
- seq 5: `ff 00 00 00` (1B content `ff` + 3B CRC)
- seq 6: `03 04 00 00 00 01 00 00 00 [3B CRC]` (field 0 = 1 marker)
- seq 7+: channel entries — **same TLV format as session 2 channel catalog** (`04 [size_LE4] [idx] v1/gameData/<name>`) plus a final `06 04 00 00 00 [count_LE4]` total

Total: ~250 chunks, ~5.3 KB per capture. Session 1 is **NOT** for device description — that lives on session 2 only in this firmware. Sim currently emits session1_desc on session 1, which is the opposite of what real wheel does.

Session 0x02 device→host starts with device description (24/5/2/9/2 byte chunks for CSP — **not** the VGS 26/5/2/9/2 split), then continues with channel catalog + zlib streams.

### CSP session 2 desc chunk sizes: 24/5/2/9/2 (42B), not 26/5/2/9/2 (44B)

CSP session1_desc byte-exact from capture seq 6-10:

```
chunk 1 (24B): 07 01 00 00 00 00 0c 04 8a e5 d0 86 b2 fc ad 74 86 db e2 08 04 10 01 0a
chunk 2  (5B): 01 64 00 00 00
chunk 3  (2B): 05 00
chunk 4  (9B): 04 02 00 00 00 00 00 00 00
chunk 5  (2B): 06 00
```

Last byte of chunk 1 is `0a`, not `05`. The `05` seen in some older notes is a mistranscription — real CSP desc ends chunk 1 with `0a` (TLV type 0x0a marker starting the next logical block).

### `43:17:fc:00:*` probe family is host-side ACK, NOT a real probe

`43:17:FC:00:[sess][seq_lo][seq_hi]` appearing 1000+ times in captures is PitHouse emitting its own cumulative ACK back to the wheel — not a probe expecting a response. Sim shouldn't answer these from a replay table; the replay's capture-time ack value is stale. Consume silently in `_handle_wheel`.

### Hub/base identity probes are 0x00-prefixed, not empty-payload

Sim's `_build_identity_tables` keys pithouse_rsp by `(grp, payload_suffix)` where wheel probes use suffix `b'\x01'` (sub 01). Hub/base probes on grp `02/04/05/06/07/08/0f/10/11` to dev `0x12` / `0x13` use suffix `b''` (empty) with LEN=1 frames. Response payload IS still 16-byte identity string. Sim's wheel-only pithouse_rsp doesn't match dev 0x12/0x13 probes — they fall through to replay table, which works when populated from a matching-wheel capture.

### hw_id must match between session1_desc and cmd 0x06

Real wheel embeds 12-byte display hw_id inside session1_desc at byte offset 8. Sim's cmd 0x06 response returns `model['display']['hw_id']`. Randomising either (mcp_server `_apply_model` did this on every sim_start for cache-busting) creates a mismatch that PitHouse detects and downgrades display detection. Fix: profile-defined hw_id used verbatim, no randomisation.

### enableManager.dashboards — factory-populated on empty FS

Real wheel on empty FS still advertises 11 factory-baked dashboards with full metadata in `enableManager.dashboards` (hash, id, idealDeviceInfos, previewImageFilePaths, title). configJson state payload ~7.2 KB uncompressed. Sim previously emitted 454 B (empty enableManager.dashboards) which leaves Dashboard Manager's UI in a half-state. Fix: fall back to factory's enableManager.dashboards entries from `sim/factory_configjson_state.json` when FS has no user-uploaded content.

Keep `disableManager`, `imageRefMap`, `imagePath` empty — copying those from factory state regressed session 0x03 open retry loop.

### Session 0x06 file-transfer paths (2026-04 firmware only)

In the 2026-04 CSP capture, session 0x06 carries **UTF-16LE file paths + MD5 hashes** as part of the file-transfer staging:

```
C:/Users/Tove/AppData/Local/Temp/_moza_filetransfer_tmp_1777043733868
p/_moza_filetransfer_md5_a6f0ff161012456174ef5060fcba280b
```

267 blobs, 14.7 KB per capture. Sim currently treats session 0x06 as a simple keepalive (via `_DEVICE_SESSIONS`). Earlier firmware captures had session 0x06 as keepalive-only — this is a newer-firmware expansion of the session's role.

### Channel catalog TLV framing

`_build_session2_message` format, verified against capture bytes:

```
FF                          marker
03 04 00 00 00 01 00 00 00  field 0 = 1
04 [size_LE4] [idx_u8] [url_ascii...]  channel entry, one per URL
03 04 00 00 00 02 00 00 00  field 0 = 2
06 04 00 00 00 [count_LE4]  total-channel-count
```

Wheel emits this on BOTH session 0x01 and session 0x02 (real CSP, 2026-04). Sim emits on session 0x02 only.

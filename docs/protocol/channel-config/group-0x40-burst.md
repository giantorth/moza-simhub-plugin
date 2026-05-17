## Channel configuration burst (group 0x40, post-upload or on connect)

After dashboard file transfer (or on wheel connect without upload), Pit House sends burst of `0x40` commands configuring channel layout. Same burst used both contexts; CS V2.1 (no screen) receives same channel config as VGS (built-in screen). Channel indices and response values are dashboard-specific.

| Cmd | Data | Purpose |
|-----|------|---------|
| `09:00` | (none) | Begin/reset channel config |
| `1e:00` | `CC 00 00` | Enable channel CC on page 0 — wheel responds `CC XXXX` (stored, e.g. `01f4`=500, `03e8`=1000, `0bb8`=3000) |
| `1e:01` | `CC 00 00` | Enable channel CC on page 1 |
| `1e:03` | `CC 00 00` | Enable channel CC on page 3 (page 2 unused) |
| `1c:00`/`1c:01` | `00` | Page configuration |
| `1d:00`/`1d:01` | `00` | Page configuration |
| `28:00` | `00` | Query active dashboard mode (wheel retains across power cycles) |
| `28:01` | `00` | Query active page number |
| `28:02` | `01 00` | Set multi-channel telemetry mode (01=multi, 00=RPM only) |
| `1b:00`/`1b:01` | `FF value` | Brightness per page (value `64`=100%) |
| `1f:00`/`1f:01` | `FF idx 00 00 00` | LED color read per index (`idx`=`0a`–`0f` observed) |
| `27:00`–`27:03` | `00/01 XX YY ZZ` | Per-page dashboard binding fingerprint (read via 0x40, set via 0x3F — see § 27:NN per-page binding state below) |
| `29:00` | `00` | Display settings (TBD) |
| `2a:03` | `00` | Display settings (TBD) |
| various | — | Other display settings (`0a`, `0b`, `05`, `20`, `21`, `24`, etc.) |

Wheel `0x0e` debug log confirms channel config writes EEPROM: `"Table 2, Param 47 Written: 7614374"`.

**Cold-connect (no dashboard upload):** captured during CS → VGS swap in `cs-to-vgs-wheel.ndjson`. Pit House runs full identity probe then same channel configuration burst — no `7c:00` file transfer or `configJson()` RPC. Does not ask wheel which dashboard is active; pushes channel layout from internal state. `0xc0/13:00` response `00 ff ff` during setup may indicate "no active dashboard" or default state.

**Implication:** burst appears required on each wheel connection before telemetry frames accepted. Sending `7d:23` to fresh wheel without first sending `0x40` channel enables and `28:02 data=0100` may not work.

### Page × channel matrix

PitHouse cold-attach capture (R5 base, W17 wheel, 2026-04-29) emitted `1E` enables across **3 pages × 5 channels = 15 combos**:

```
pages    used: 0x00, 0x01, 0x03      (skips 0x02)
channels used: 0x02, 0x03, 0x04, 0x05, 0x06   per page
```

Likely meaning of `page` and `channel`:
- `page` = update-rate / tier bucket. 3 pages may correspond to the 3 known `package_level` values `30 / 500 / 2000` ms (see [`../../Telemetry/Dashboard/DashboardProfileStore.cs`]).
- `channel` = sub-stream slot within bucket. 5 slots per page → 15 max simultaneous wheel-bound telemetry channels.

### 28:00/28:01/28:02 details

| Wire | Name (rs21_parameter.db) | Purpose |
|------|--------------------------|---------|
| `28:00 data=00` | `WheelGetCfg_GetMultiFunctionSwitch` | Query active dashboard mode. Wheel retains last loaded dashboard across disconnections. |
| `28:01 data=00` | `WheelGetCfg_GetMultiFunctionNum` | Query active page number |
| `28:02 data=01:00` | `WheelGetCfg_GetMultiFunctionLeft` | Set multi-channel telemetry mode (01=multi, 00=RPM only) |

Read-then-write pattern: Pithouse sends 28:00 and 28:01 (read state), then 28:02 (set mode) during burst. Wheel responds `00:00` to `28:02 data=01:00` — normal behavior, not failure.

**Normal operation:** `28 02 01 00` continues polling ~3.4 Hz to maintain multi-channel mode.

### 27:NN per-page binding fingerprint (host↔wheel on groups 0x40 / 0x3F)

Per-page dashboard binding fingerprint observed in `wireshark/csp/startup, change knob colors, change dash several times, delete dash.pcapng`. Read via group `0x40`, written via group `0x3F`:

```
read  : 7e 03 40 17 27 [page] 00            host→wheel    (group 0x40)
reply : 7e 06 c0 71 27 [page] [4-byte data] wheel→host    (group 0xc0, dev 0x71 = nibble-swap of 0x17)
write : 7e 06 3f 17 27 [page] [4-byte data] host→wheel    (group 0x3F)
```

`page` = 0..3. `4-byte data` = `[flag:1] [3-byte fingerprint]`. Flag byte:
- `0x00` — primary fingerprint (active state)
- `0x01` — alternate fingerprint (cached / counter state)

Wheel poll responses oscillate between `00 XX YY ZZ` and `01 XX YY ZZ` for the same page across consecutive `27:NN` reads — wheel returns both states alternately.

Captured sequence:
```
t=49.57s   page 3 ← 00 f6 b4 99    (initial bind at startup)
t=49.59s   page 1 ← 00 c6 e1 9b
t=49.60s   page 0 ← 00 f3 79 a1
t=49.61s   page 2 ← 00 9b a7 eb
t=49.88s   page 2 ← 01 11 47 e6    (alternate-state set)
t=150.57s  page 2 ← 00 ff 64 00
t=166.78s  page 3 ← 00 5f 97 ff
```

The 24-bit fingerprint is wheel-internal and does not match any session 0x09 configJson field (`id`, `dirName`, `title`, `hash`) under MD5/SHA1/SHA256/CRC32. Origin unknown — likely assigned at upload time or computed via a proprietary hash.

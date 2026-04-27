## Channel configuration burst (group 0x40, post-upload or on connect)

After dashboard file transfer (or on wheel connect without upload), Pit House sends burst of `0x40` commands configuring channel layout. Same burst used both contexts; CS V2.1 (no screen) receives same channel config as VGS (built-in screen). Channel indices and response values are dashboard-specific.

| Cmd | Data | Purpose |
|-----|------|---------|
| `09:00` | (none) | Begin/reset channel config |
| `1e:01` | `CC 00 00` | Enable channel CC on page 1 |
| `1e:00` | `CC 00 00` | Enable channel CC on page 0 — wheel responds `CC XXXX` (stored, e.g. `01f4`=500, `03e8`=1000, `0bb8`=3000) |
| `1c:00`/`1c:01` | `00` | Page configuration |
| `1d:00`/`1d:01` | `00` | Page configuration |
| `28:00` | `00` | Query active dashboard mode (wheel retains across power cycles) |
| `28:01` | `00` | Query active page number |
| `28:02` | `01 00` | Set multi-channel telemetry mode (01=multi, 00=RPM only) |
| `1b:00`/`1b:01` | `FF value` | Brightness per page (value `64`=100%) |
| `1f:00`/`1f:01` | `FF idx 00 00 00` | LED color read per index (`idx`=`0a`–`0f` observed) |
| `27:00`–`27:03` | `00/01 00 00 00` | Page/dashboard config (sub-IDs 0–3, variants with `01`) |
| `29:00` | `00` | Display settings (TBD) |
| `2a:03` | `00` | Display settings (TBD) |
| various | — | Other display settings (`0a`, `0b`, `05`, `20`, `21`, `24`, etc.) |

Wheel `0x0e` debug log confirms channel config writes EEPROM: `"Table 2, Param 47 Written: 7614374"`.

**Cold-connect (no dashboard upload):** captured during CS → VGS swap in `cs-to-vgs-wheel.ndjson`. Pit House runs full identity probe then same channel configuration burst — no `7c:00` file transfer or `configJson()` RPC. Does not ask wheel which dashboard is active; pushes channel layout from internal state. `0xc0/13:00` response `00 ff ff` during setup may indicate "no active dashboard" or default state.

**Implication:** burst appears required on each wheel connection before telemetry frames accepted. Sending `7d:23` to fresh wheel without first sending `0x40` channel enables and `28:02 data=0100` may not work.

### 28:00/28:01/28:02 details

| Wire | Name (rs21_parameter.db) | Purpose |
|------|--------------------------|---------|
| `28:00 data=00` | `WheelGetCfg_GetMultiFunctionSwitch` | Query active dashboard mode. Wheel retains last loaded dashboard across disconnections. |
| `28:01 data=00` | `WheelGetCfg_GetMultiFunctionNum` | Query active page number |
| `28:02 data=01:00` | `WheelGetCfg_GetMultiFunctionLeft` | Set multi-channel telemetry mode (01=multi, 00=RPM only) |

Read-then-write pattern: Pithouse sends 28:00 and 28:01 (read state), then 28:02 (set mode) during burst. Wheel responds `00:00` to `28:02 data=01:00` — normal behavior, not failure.

**Normal operation:** `28 02 01 00` continues polling ~3.4 Hz to maintain multi-channel mode.

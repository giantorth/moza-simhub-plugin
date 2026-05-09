### Group `0x29` host base-settings write (host → dev `0x13`, once during config)

Group `0x29` = `0x28 | 0x01` — the **write** companion of group `0x28`
(base-settings read). Same 2-byte int command IDs as group `0x28`; payload
contains the new value.

See [`../devices/wheelbase-0x13.md` § Group `0x28` / `0x29`](../devices/wheelbase-0x13.md)
for the full per-command table (FFB strength, inertia, damper, friction,
spring, road sensitivity, etc.).

**Frame layout (request):**

```
7E [N] 29 13 [cmd_id 1..2 B] [value bytes] [checksum]
```

**Frame layout (response):**

```
7E [N] A9 31 [cmd_id echo] [value echo] [checksum]
```

`A9` = `0x29 | 0x80`; `0x31` = nibble-swap of `0x13`. Response payload
mirrors request payload byte-for-byte.

**Observed during dashboard config burst** (`connect-wheel-start-game.json`,
single occurrence per connect):

| Cmd | Value | DB name | Decoded |
|-----|-------|---------|---------|
| `0x13` | `04 4C` | `natural-inertia` | BE u16 = 1100 |

`0x13` is the `natural-inertia` setting on the wheelbase (hands-off
protection); writing `1100` likely sets a default safety threshold during
PitHouse's config phase. Other group-`0x29` writes are theoretically possible
(any of the cmd IDs from group `0x28`) but only this one has been observed
during connect.

Group `0x29` is **not periodic** — only one write seen per session,
distinguishing it from groups `0x1F`, `0x28`, `0x2B` which poll continuously.

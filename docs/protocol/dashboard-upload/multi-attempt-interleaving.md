### Multi-attempt interleaving in the buffer

PitHouse retransmits cause chunks from different upload attempts to coexist in the session buffer. Each attempt has its own chunk0 (counter=0). Continuation chunks from one attempt do NOT cleanly continue another attempt's deflate stream — they belong to a different zlib instance.

Sim's strategy (`_parse_upload_6b` in `sim/wheel_sim.py`):

1. Walk the buffer with the 6B-header validator (`type ∈ {01,02,03,11}`, `pad == 00 00 00`, stride matches).
2. Find chunk0 = the first type=0x03 chunk where `body[281:284] == 0` AND `78 9c` magic is present.
3. Initialise `zlib.decompressobj()` with `body0[zoff:]`.
4. Greedily extend: for each remaining type=0x03 chunk, scan continuation offsets in [280, 1500) and pick the offset that produces the longest clean decompression extension. Append that chunk's contribution.
5. Repeat until no more chunks extend the stream OR `decompressobj.eof`.

This handles partial uploads (PitHouse aborted mid-flight) and retransmit interleaving — the greedy walk stops when no chunk fits cleanly. Verified: 62KB session 0x09 buf with 14 type=0x03 chunks (mostly retransmits) → 82KB decoded mzdash JSON, root keys including `name='JDM Gauge Style 02'`, `version='1.1.1'`, `type='Window.qml'`.

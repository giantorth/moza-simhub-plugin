"""Byte-exact tier-def reference extractor.

For each capture, build:
  - canonical session-0x01 byte stream (each seq's bytes, first occurrence only)
  - chronological list of emissions (each emission = a contiguous run of seqs h2b'd
    in one short window; on retx of the same seqs we skip)
  - dashboard-active timeline (7d23 byte6 tracking)
  - TLV entries with absolute byte offset in the canonical stream

Output: one section per emission, with raw hex of new bytes added in that window
plus parsed TLV summary. This is the byte-diff reference for Telemetry2 tests.
"""
import sys, json, hashlib, os
sys.path.insert(0, '/tmp')
import parse_tlv as tlv

path = sys.argv[1]
session_filter = int(sys.argv[2], 16) if len(sys.argv) > 2 else 0x01
out_path = sys.argv[3] if len(sys.argv) > 3 else None

# Pass 1: collect all session-0x01 type=0x01 chunks AND dash-display state events
seq_first = {}      # seq -> (t, bytes) first time we saw it
seq_appearances = {}  # seq -> [(t, bytes_hash)] for retx audit
dash_phases = []    # [(t, byte6)] when changes
prev_b6 = None
t0 = None

with open(path) as f:
    for L in f:
        try: j = json.loads(L)
        except: continue
        if j.get('dir') != 'h2b': continue
        if j.get('grp') != 0x43 or j.get('dev') != 0x17: continue
        pay = j.get('payload', '')
        t = j.get('t', 0.0)
        if t0 is None: t0 = t

        # dash-active state probe: 7d23 byte6
        if pay.startswith('7d23') and len(pay) >= 14:
            b6 = int(pay[12:14], 16)
            if b6 != prev_b6:
                dash_phases.append((t, t - t0, b6))
                prev_b6 = b6
            continue

        # session chunk: 7c00 [sess] [type] [seq lo hi] [data] [crc32] (in payload-hex)
        if not pay.startswith('7c00') or len(pay) < 16: continue
        ses = int(pay[4:6], 16)
        if ses != session_filter: continue
        typ = int(pay[6:8], 16)
        if typ != 0x01: continue
        seq = int(pay[10:12] + pay[8:10], 16)
        # data is everything between byte-offset 12 and last 8 hex (CRC32 trailer)
        if len(pay) < 12 + 8: continue
        data_hex = pay[12:-8]
        data = bytes.fromhex(data_hex)
        h = hashlib.sha1(data).hexdigest()[:10]
        seq_appearances.setdefault(seq, []).append((t, h, len(data)))
        if seq not in seq_first:
            seq_first[seq] = (t, data)

# Pass 2: build canonical stream by ascending seq
seqs_sorted = sorted(seq_first.keys())
stream = bytearray()
seq_offsets = {}    # seq -> (offset_in_stream, length)
for s in seqs_sorted:
    seq_offsets[s] = (len(stream), len(seq_first[s][1]))
    stream.extend(seq_first[s][1])
stream = bytes(stream)

# Pass 3: parse TLV against canonical stream
entries = tlv.parse_msg(stream)

# Pass 4: build emission windows. PitHouse emits a message via several seqs. After
# a flush, more seqs are added. We define an "emission" as a window where seqs
# arrive within ~50ms of each other; a gap >100ms starts a new emission.
emissions = []
cur = []
prev_t = None
for s in seqs_sorted:
    t, _ = seq_first[s]
    if prev_t is None or (t - prev_t) < 0.150:
        cur.append(s)
    else:
        emissions.append(cur)
        cur = [s]
    prev_t = t
if cur: emissions.append(cur)

# Pass 5: locate which TLV entries fall within each emission's byte range
def emission_byterange(seqs):
    if not seqs: return (0, 0)
    o0 = seq_offsets[seqs[0]][0]
    last = seqs[-1]
    o1 = seq_offsets[last][0] + seq_offsets[last][1]
    return (o0, o1)

# Pass 6: dash phase at time t
def dash_at(t):
    cur = None
    for tphase, _dt, b6 in dash_phases:
        if tphase <= t:
            cur = b6
        else:
            break
    return cur

# Output
buf = []
def emit(s=''): buf.append(s)

emit(f"# Byte-exact tier-def reference: {os.path.basename(path)}")
emit(f"")
emit(f"Session 0x{session_filter:02x}, h2b grp=0x43 dev=0x17 type=0x01 (tier-def TLV stream).")
emit(f"")
emit(f"Total unique seqs collected: {len(seqs_sorted)} (range {min(seqs_sorted)}..{max(seqs_sorted)}).")
emit(f"Total canonical-stream bytes: {len(stream)}.")
emit(f"")

emit(f"## Dashboard-active timeline (7d23 byte6)")
emit(f"")
emit(f"| time (rel) | byte6 |")
emit(f"|-----------:|:-----:|")
for t, dt, b6 in dash_phases:
    emit(f"| +{dt:7.2f}s   | 0x{b6:02x}  |")
emit(f"")

emit(f"## Retransmission audit (seqs with > 1 appearance)")
emit(f"")
emit(f"| seq | appearances | bytes (first/all match) |")
emit(f"|----:|-----------:|:------------------------|")
retx = [(s, a) for s, a in seq_appearances.items() if len(a) > 1]
retx.sort()
for s, apps in retx[:20]:
    hashes = set(h for _, h, _ in apps)
    sizes = set(L for _, _, L in apps)
    match = "all-identical" if len(hashes) == 1 else "DIFFER"
    emit(f"| {s} | {len(apps)} | {match} ({len(sizes)} sizes) |")
if len(retx) > 20:
    emit(f"| ... | {len(retx) - 20} more |  |")
emit(f"")

emit(f"## Canonical stream — TLV entries")
emit(f"")
off = 0
emission_idx = 0
emission_ranges = [emission_byterange(e) for e in emissions]

for entry in entries:
    if isinstance(entry[0], str):
        emit(f"  [@{off:5d}] !{entry[0]} {entry[1]}")
        break  # truncation
    tag, size, val = entry
    rec_total = 1 + 4 + size
    desc = tlv.fmt(tag, size, val).strip()
    # which emission does this offset fall into?
    em_idx = None
    for i, (o0, o1) in enumerate(emission_ranges):
        if o0 <= off < o1:
            em_idx = i
            break
    em_tag = f"E{em_idx}" if em_idx is not None else "—"
    emit(f"  [@{off:5d} {em_tag}] {desc}")
    off += rec_total

emit(f"")
emit(f"## Emissions (chronological windows)")
emit(f"")
emit(f"| # | time (rel) | seq range | bytes | dash byte6 | preview |")
emit(f"|:--|----------:|:---------:|------:|:----------:|:--------|")
for i, seqs in enumerate(emissions):
    t = seq_first[seqs[0]][0]
    o0, o1 = emission_ranges[i]
    raw = stream[o0:o1]
    preview = raw[:32].hex()
    if len(raw) > 32:
        preview += '...'
    b6 = dash_at(t)
    b6s = f"0x{b6:02x}" if b6 is not None else "?"
    emit(f"| E{i} | +{t-t0:7.2f}s | {seqs[0]}..{seqs[-1]} | {o1-o0} | {b6s} | `{preview}` |")
emit(f"")

emit(f"## Per-emission raw bytes (full hex)")
emit(f"")
for i, seqs in enumerate(emissions):
    o0, o1 = emission_ranges[i]
    raw = stream[o0:o1]
    t = seq_first[seqs[0]][0]
    b6 = dash_at(t)
    b6s = f"0x{b6:02x}" if b6 is not None else "?"
    emit(f"### E{i} t+{t-t0:.2f}s seq={seqs[0]}..{seqs[-1]} dash=byte6={b6s} ({o1-o0}B)")
    emit(f"")
    emit(f"```")
    # 32 bytes per line
    for off2 in range(0, len(raw), 32):
        emit(raw[off2:off2+32].hex())
    emit(f"```")
    emit(f"")

text = '\n'.join(buf)
if out_path:
    with open(out_path, 'w') as f:
        f.write(text)
    print(f"Written {out_path} ({len(text)}B)")
else:
    print(text)

"""Audit: does any chunk in the bridge captures require the 1-byte-prefix CRC layout
(SessionDataReassembler.cs:78 fallback) instead of the standard full-payload layout?

For each h2b session-data chunk in each capture, compute CRC32 two ways:
  layout A: CRC over chunk[0..-4]      (standard — what new builder emits)
  layout B: CRC over chunk[1..-4]      (1-byte-prefix variant)
Compare against the trailing 4-byte CRC.

If layout A matches every chunk, the dual-CRC heuristic can be dropped.
If any chunk matches only layout B, must keep both."""
import sys, json, zlib, collections

paths = sys.argv[1:] if len(sys.argv) > 1 else [
    'sim/logs/bridge-20260503-112940.jsonl',
    'sim/logs/bridge-20260503-113353.jsonl',
    'sim/logs/bridge-20260503-113616.jsonl',
    'sim/logs/bridge-20260503-115840.jsonl',
]

def crc32(data):
    return zlib.crc32(data) & 0xFFFFFFFF

totals = collections.Counter()
per_session_layout = collections.defaultdict(lambda: collections.Counter())

for path in paths:
    print(f"\n=== {path} ===")
    counts = collections.Counter()
    layout_b_only_examples = []
    neither_examples = []
    with open(path) as f:
        for L in f:
            try: j = json.loads(L)
            except: continue
            d = j.get('dir')
            if d not in ('h2b', 'b2h'): continue
            pay = j.get('payload', '')
            if not pay.startswith('7c00') or len(pay) < 16: continue
            ses = int(pay[4:6], 16)
            typ = int(pay[6:8], 16)
            if typ != 0x01: continue
            seq = int(pay[10:12]+pay[8:10], 16)
            chunk = bytes.fromhex(pay[12:])
            if len(chunk) < 5: continue
            wire = int.from_bytes(chunk[-4:], 'little')
            crc_a = crc32(chunk[:-4])
            crc_b = crc32(chunk[1:-4]) if len(chunk) >= 5 else None
            key = (d, ses)
            if crc_a == wire:
                counts['layout_A'] += 1
                per_session_layout[key]['A'] += 1
            elif crc_b == wire:
                counts['layout_B_only'] += 1
                per_session_layout[key]['B'] += 1
                if len(layout_b_only_examples) < 3:
                    layout_b_only_examples.append((d, ses, seq, chunk[:32].hex()))
            else:
                counts['neither'] += 1
                per_session_layout[key]['none'] += 1
                if len(neither_examples) < 3:
                    neither_examples.append((d, ses, seq, chunk[:32].hex()))
    print(f"  layout A (standard): {counts['layout_A']}")
    print(f"  layout B only (1-byte prefix): {counts['layout_B_only']}")
    print(f"  neither (corrupt/truncated): {counts['neither']}")
    if layout_b_only_examples:
        print(f"  layout B examples:")
        for d, s, seq, hx in layout_b_only_examples:
            print(f"    {d} ses=0x{s:02x} seq={seq}: {hx}")
    if neither_examples:
        print(f"  neither examples:")
        for d, s, seq, hx in neither_examples:
            print(f"    {d} ses=0x{s:02x} seq={seq}: {hx}")

print()
print(f"=== Per (direction, session) layout breakdown ===")
for (d, s), bd in sorted(per_session_layout.items()):
    print(f"  {d} ses=0x{s:02x}: {dict(bd)}")

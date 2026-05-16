# Findings 2026-05-15 — sess=0x01 type=0x05 string values are UTF-8, not ASCII

The 2026-05-14 sess=0x01 protocol decode documented the string-value transport
as "ASCII only" based on the PitHouse reference capture
(`sim/logs/bridge-20260514-204307.jsonl`), which contained only the values
`imola` and `ks_laguna_seca` — both pure ASCII. That wording was a
sample-size artefact. Live testing against the CS Pro wheel on 2026-05-15
proves the encoding is **UTF-8**.

## Symptom that drove the test

After deploying string support, real game-side track names like Codemasters
Dirt Rally's `Viñedos dentro del valle Parra` and `Col de Turini Départ`
rendered on the wheel as `Vi?edos dentro del valle Parra` and
`Col de Turini D?part` — the accented characters were getting silently
replaced with `?`. Cause: `Telemetry/Frames/StringValueBuilder.cs:53` was
using `Encoding.ASCII.GetBytes()`, whose default `EncoderFallback` replaces
any codepoint above 0x7F with the literal byte `0x3F` (`?`).

## Glyph coverage (CS Pro, 2026-04+ firmware)

A second test with a torture-prefix `SimHub áéíñ àèìò âêîô äëïü ñçß æœø €£¥ °±× ←→ 中文 αβ ЯБ 🏁`
(~95 prefix bytes plus channel name) sampled what's in the firmware font:

- **Renders**: Latin Extended-A/B accented characters (acute, grave,
  circumflex, diaeresis, tilde, cedilla, stroked b), sharp s, ligatures
  (æ œ ø), currency (€ £ ¥), math symbols (° ± ×), arrows (← →), CJK
  ideographs (中 文), Greek (α β), Cyrillic (Я Б). The wheel decoded the
  multi-byte UTF-8 sequences and produced the correct glyph for every
  one of these.
- **Does NOT render**: the emoji 🏁 (U+1F3C1,
  encoded `F0 9F 8F 81` in UTF-8) showed as a missing-glyph placeholder.
  Either the decoder doesn't accept 4-byte UTF-8 sequences or the font
  has no slot above U+FFFF. Either way: **anything beyond BMP is off
  the menu** for wheel display.

Practical implication: BMP-only is safe; supplementary (emoji,
math alphanumerics in `1D000-1D7FF`, CJK Extension B+ in `20000+`)
should be avoided in any value that reaches the wheel.

## Open items

- **Other UTF-8 fields**: type=0x04 catalog URLs (`v1/gameData/SpeedKmh` style)
  are pure ASCII by design — no test needed. But sess=0x02 kind=10 strings
  are documented as UTF-16BE elsewhere (see
  [`2026-05-15-sess02-kind8-tlv-and-preset-block.md`](2026-05-15-sess02-kind8-tlv-and-preset-block.md));
  the two sessions clearly use different encoding conventions.
- **4-byte UTF-8 acceptance**: untested whether the wheel's parser
  *rejects* supplementary-plane bytes outright (treats `F0 9F 8F 81` as
  four invalid bytes producing four placeholders) or *decodes them and
  has no glyph* (one decoded codepoint, one placeholder). Both look
  identical on the wire response; would need a captured value with a
  known supplementary-plane codepoint followed by ASCII to tell apart.
- **Multi-byte truncation behaviour**: untested. The 127-byte cap will
  occasionally land mid-sequence for long names with multi-byte chars;
  documenting "renders as placeholder" is plausible from the Latin-1
  result but not directly observed.

### Tag 0x03 — config parameter

Tag 0x03 has different values depending on direction and version:

| Direction | Version | Value | Interpretation |
|-----------|---------|-------|---------------|
| Wheel → Host | 0 | 1 | Constant across VGS and CSP |
| Host → Wheel | 0 (CSP) | 1 | Mirrors wheel value |
| Host → Wheel | 2 (VGS) | 0 | Different meaning in version 2 context |

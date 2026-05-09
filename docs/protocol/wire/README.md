# Wire format

Foundational frame layout used by every device on the bus. Read this before any other folder.

| File | Topic |
|------|-------|
| [`frame-format.md`](frame-format.md) | Frame header (`7E [N] [group] [device] [payload] [checksum]`), response transforms, command chaining |
| [`checksum.md`](checksum.md) | Checksum algorithm, `0x7E` byte stuffing escape rules |
| [`wheel-write-echoes.md`](wheel-write-echoes.md) | Wheel-side firmware echoes that look like responses but carry no read-back semantics |

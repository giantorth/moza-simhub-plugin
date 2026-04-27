## Setting value encoding

Several configuration commands use non-obvious value encoding. Confirmed by cross-referencing Pithouse USB captures with boxflat source.

### Wheel settings (group 0x3F/0x40, device 0x17)

| Command | ID | Raw values | Notes |
|---------|-----|-----------|-------|
| paddles-mode | `03` | 1=Buttons, 2=Combined, 3=Split | **1-based**. Sending 0 is invalid — causes firmware to break all paddle input including shift paddles |
| stick-mode | `05` | 0=Buttons, 256=D-Pad | 2-byte field; D-Pad sets high byte (`0x0100`) |
| rpm-indicator-mode | `04` | 1=RPM, 2=Off, 3=On | **1-based** (wheel only) |

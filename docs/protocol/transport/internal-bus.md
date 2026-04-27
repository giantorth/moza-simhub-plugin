## Internal bus topology (monitor.json)

`monitor.json` file in Pit House installation defines device tree for each base model. These are **internal bus IDs**, not serial protocol device IDs. Mapping: bus 2 → main (0x12), bus 3 → base (0x13), bus 4 → wheel (0x17), bus 5 → dash (0x14), etc.

Common topology (single-controller bases):
```
1 (USB host)
└── 2 (Main controller / hub)
    ├── 3 (Motor controller)
    ├── 4 (Wheel) ── 18 (Wheel display unit)
    ├── 5 (Dashboard) ── 17 (Dash sub-device)
    ├── 6..12 (Peripheral ports)
    ├── 13, 14 (children of 9)
    └── 16 (child of 7)
```

D11 (R21/R25/R27 Ultra) omits bus 5; S09 CM2 dash connects as bus 19 directly off bus 2.

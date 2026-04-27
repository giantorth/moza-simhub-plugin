### Hub (dev 0x12) and base (dev 0x13) identity are byte-identical

Confirmed on both CSP R9 and KSP R12 captures: real wheelbase answers dev 0x12 and dev 0x13 probes with the **same** identity values. They are aliases for a single physical device. Sim's `_build_device_identity` installs the `base_identity` block under both dev IDs accordingly. Do not synthesize differing values.
